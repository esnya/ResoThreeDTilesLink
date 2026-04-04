using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class TileRunCoordinator(
        ITilesSource tilesSource,
        TraversalPlanner traversalPlanner,
        IContentProcessor contentProcessor,
        IMeshPlacementService meshPlacementService,
        IResoniteSession resoniteSession,
        IGoogleAccessTokenProvider googleAccessTokenProvider,
        ILogger<TileRunCoordinator> logger) : ITileRunCoordinator
    {
        private readonly ITilesSource _tilesSource = tilesSource;
        private readonly TraversalPlanner _traversalPlanner = traversalPlanner;
        private readonly IContentProcessor _contentProcessor = contentProcessor;
        private readonly IMeshPlacementService _meshPlacementService = meshPlacementService;
        private readonly IResoniteSession _resoniteSession = resoniteSession;
        private readonly IGoogleAccessTokenProvider _googleAccessTokenProvider = googleAccessTokenProvider;
        private readonly ILogger<TileRunCoordinator> _logger = logger;

        public async Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            GoogleTilesAuth auth = await BuildAuthAsync(request, cancellationToken).ConfigureAwait(false);
            if (!request.Output.DryRun && request.Output.ManageConnection)
            {
                _logger.LogInformation("Connecting to Resonite Link at {Host}:{Port}", request.Output.Host, request.Output.Port);
                await _resoniteSession.ConnectAsync(request.Output.Host, request.Output.Port, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                _logger.LogInformation("Fetching root tileset from Google Map Tiles API.");
                await SetProgressAsync(request, 0f, "Fetching root tileset...", cancellationToken).ConfigureAwait(false);
                Tiles.Tileset rootTileset = await _tilesSource.FetchRootTilesetAsync(auth, cancellationToken).ConfigureAwait(false);
                _traversalPlanner.Initialize(rootTileset, request);
                await SetProgressAsync(request, _traversalPlanner.GetSummary(), cancellationToken).ConfigureAwait(false);

                while (_traversalPlanner.TryPlanNext(out PlannerCommand? command) && command is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PlannerResult result = await ExecuteCommandAsync(command, request, auth, cancellationToken).ConfigureAwait(false);
                    _traversalPlanner.ApplyResult(result);
                    await SetProgressAsync(request, _traversalPlanner.GetSummary(), cancellationToken).ConfigureAwait(false);
                }

                RunSummary summary = _traversalPlanner.GetSummary();
                await SetProgressAsync(request, summary, cancellationToken, completed: true).ConfigureAwait(false);
                return summary;
            }
            finally
            {
                if (!request.Output.DryRun && request.Output.ManageConnection)
                {
                    await _resoniteSession.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<PlannerResult> ExecuteCommandAsync(
            PlannerCommand command,
            TileRunRequest request,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            return command switch
            {
                ProcessTileContentCommand process => await ExecuteProcessTileContentAsync(process, request, auth, cancellationToken).ConfigureAwait(false),
                StreamPlacedMeshesCommand stream => await ExecuteStreamPlacedMeshesAsync(stream, cancellationToken).ConfigureAwait(false),
                RemoveSlotsCommand remove => await ExecuteRemoveSlotsAsync(remove, cancellationToken).ConfigureAwait(false),
                UpdateLicenseCreditCommand update => await ExecuteUpdateLicenseCreditAsync(update, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported planner command type: {command.GetType().Name}")
            };
        }

        private async Task<PlannerResult> ExecuteProcessTileContentAsync(
            ProcessTileContentCommand command,
            TileRunRequest request,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            try
            {
                ContentProcessResult content = await _contentProcessor.ProcessAsync(command.Tile, auth, cancellationToken).ConfigureAwait(false);
                return content switch
                {
                    NestedTilesetContentProcessResult nested => new NestedTilesetLoadedResult(command.Tile, nested.Tileset),
                    RenderableContentProcessResult renderable => await ExecuteRenderableContentAsync(command.Tile, renderable, request, cancellationToken).ConfigureAwait(false),
                    SkippedContentProcessResult skipped => new ContentSkippedResult(command.Tile, skipped.Reason),
                    _ => throw new InvalidOperationException($"Unsupported content process result: {content.GetType().Name}")
                };
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return new ContentSkippedResult(command.Tile, Error: ex);
            }
            catch (Exception ex)
            {
                return new ContentFailedResult(command.Tile, ex);
            }
        }

        private async Task<PlannerResult> ExecuteRenderableContentAsync(
            TileSelectionResult tile,
            RenderableContentProcessResult renderable,
            TileRunRequest request,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<PlacedMeshPayload> placedMeshes = _meshPlacementService.Place(
                tile,
                renderable.Meshes,
                request.Reference,
                request.Output.MeshParentSlotId);

            if (request.Output.DryRun)
            {
                return new RenderableContentReadyResult(tile, placedMeshes.Count, [], renderable.AssetCopyright);
            }

            return await ExecuteStreamPlacedMeshesAsync(
                new StreamPlacedMeshesCommand(tile, placedMeshes, renderable.AssetCopyright),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<PlannerResult> ExecuteStreamPlacedMeshesAsync(
            StreamPlacedMeshesCommand command,
            CancellationToken cancellationToken)
        {
            var streamedSlotIds = new List<string>();
            int streamedMeshCount = 0;

            try
            {
                foreach (PlacedMeshPayload payload in command.Meshes)
                {
                    string? slotId = await _resoniteSession.StreamPlacedMeshAsync(payload, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(slotId))
                    {
                        streamedSlotIds.Add(slotId);
                    }

                    streamedMeshCount++;
                }

                return new RenderableContentReadyResult(command.Tile, streamedMeshCount, streamedSlotIds, command.AssetCopyright);
            }
            catch (Exception ex)
            {
                return new ContentFailedResult(command.Tile, ex, streamedMeshCount, streamedSlotIds, command.AssetCopyright);
            }
        }

        private async Task<PlannerResult> ExecuteRemoveSlotsAsync(RemoveSlotsCommand command, CancellationToken cancellationToken)
        {
            int failedSlotCount = 0;
            Exception? firstError = null;

            foreach (string slotId in command.SlotIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failedSlotCount++;
                    firstError ??= ex;
                    _logger.LogWarning(ex, "Failed to remove parent tile slot {SlotId} for tile {TileId}.", slotId, command.TileId);
                }
            }

            return new SlotsRemovedResult(command.StateId, command.TileId, failedSlotCount == 0, failedSlotCount, firstError);
        }

        private async Task<PlannerResult> ExecuteUpdateLicenseCreditAsync(UpdateLicenseCreditCommand command, CancellationToken cancellationToken)
        {
            try
            {
                await _resoniteSession.SetSessionLicenseCreditAsync(command.CreditString, cancellationToken).ConfigureAwait(false);
                return new LicenseUpdatedResult(command.CreditString, true, null);
            }
            catch (Exception ex)
            {
                return new LicenseUpdatedResult(command.CreditString, false, ex);
            }
        }

        private async Task<GoogleTilesAuth> BuildAuthAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return new GoogleTilesAuth(request.ApiKey, null);
            }

            string token = await _googleAccessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return new GoogleTilesAuth(null, token);
        }

        private async Task SetProgressAsync(TileRunRequest request, RunSummary summary, CancellationToken cancellationToken, bool completed = false)
        {
            if (request.Output.DryRun)
            {
                return;
            }

            await _resoniteSession.SetProgressAsync(
                request.Output.MeshParentSlotId,
                BuildProgressValue(summary, completed),
                BuildProgressText(summary, completed),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task SetProgressAsync(TileRunRequest request, float progress01, string progressText, CancellationToken cancellationToken)
        {
            if (request.Output.DryRun)
            {
                return;
            }

            await _resoniteSession.SetProgressAsync(request.Output.MeshParentSlotId, progress01, progressText, cancellationToken).ConfigureAwait(false);
        }

        private static float BuildProgressValue(RunSummary summary, bool completed)
        {
            if (completed)
            {
                return 1f;
            }

            if (summary.CandidateTiles <= 0)
            {
                return 0f;
            }

            int completedTiles = summary.ProcessedTiles + summary.FailedTiles;
            return System.Math.Clamp((float)completedTiles / summary.CandidateTiles, 0f, 1f);
        }

        private static string BuildProgressText(RunSummary summary, bool completed)
        {
            string prefix = completed ? "Completed" : "Running";
            return $"{prefix}: candidate={summary.CandidateTiles} processed={summary.ProcessedTiles} streamed={summary.StreamedMeshes} failed={summary.FailedTiles}";
        }
    }
}
