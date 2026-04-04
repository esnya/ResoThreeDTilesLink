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
            return await RunCoreAsync(request, interactiveContext: null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<InteractiveTileRunResult> RunInteractiveAsync(
            TileRunRequest request,
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            bool removeOutOfRangeTiles,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(retainedTiles);

            var interactiveContext = new InteractiveExecutionContext(retainedTiles, removeOutOfRangeTiles);

            try
            {
                RunSummary summary = await RunCoreAsync(request, interactiveContext, cancellationToken).ConfigureAwait(false);

                IReadOnlyDictionary<string, RetainedTileState> visibleTiles = _traversalPlanner.GetVisibleTiles();
                IReadOnlySet<string> selectedTileStableIds = _traversalPlanner.GetSelectedTileStableIds();

                HashSet<string> failedRemovalStateIds = await CommitInteractiveChangesAsync(
                    interactiveContext,
                    selectedTileStableIds,
                    CancellationToken.None).ConfigureAwait(false);

                IReadOnlyDictionary<string, RetainedTileState> nextRetainedTiles = BuildNextRetainedTiles(
                    retainedTiles,
                    visibleTiles,
                    selectedTileStableIds,
                    removeOutOfRangeTiles,
                    failedRemovalStateIds);

                await ApplyFinalLicenseCreditAsync(request, nextRetainedTiles, CancellationToken.None).ConfigureAwait(false);
                return new InteractiveTileRunResult(summary, nextRetainedTiles, selectedTileStableIds);
            }
            catch (OperationCanceledException)
            {
                await RollbackInteractiveChangesAsync(request, interactiveContext).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await RollbackInteractiveChangesAsync(request, interactiveContext).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<RunSummary> RunCoreAsync(
            TileRunRequest request,
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
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
                await SetProgressAsync(request, _traversalPlanner.GetProgress(), cancellationToken).ConfigureAwait(false);

                while (_traversalPlanner.TryPlanNext(out PlannerCommand? command) && command is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PlannerResult result = await ExecuteCommandAsync(command, request, auth, interactiveContext, cancellationToken).ConfigureAwait(false);
                    _traversalPlanner.ApplyResult(result);
                    await SetProgressAsync(request, _traversalPlanner.GetProgress(), cancellationToken).ConfigureAwait(false);
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
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            return command switch
            {
                ProcessTileContentCommand process => await ExecuteProcessTileContentAsync(process, request, auth, interactiveContext, cancellationToken).ConfigureAwait(false),
                StreamPlacedMeshesCommand stream => await ExecuteStreamPlacedMeshesAsync(stream, interactiveContext, cancellationToken).ConfigureAwait(false),
                RemoveSlotsCommand remove => await ExecuteRemoveSlotsAsync(remove, interactiveContext, cancellationToken).ConfigureAwait(false),
                UpdateLicenseCreditCommand update => await ExecuteUpdateLicenseCreditAsync(update, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported planner command type: {command.GetType().Name}")
            };
        }

        private async Task<PlannerResult> ExecuteProcessTileContentAsync(
            ProcessTileContentCommand command,
            TileRunRequest request,
            GoogleTilesAuth auth,
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            if (interactiveContext is not null &&
                interactiveContext.TryGetRetainedTile(command.Tile, out RetainedTileState retainedTile))
            {
                return new RenderableContentReadyResult(command.Tile, 0, retainedTile.SlotIds, retainedTile.AssetCopyright);
            }

            try
            {
                ContentProcessResult content = await _contentProcessor.ProcessAsync(command.Tile, auth, cancellationToken).ConfigureAwait(false);
                return content switch
                {
                    NestedTilesetContentProcessResult nested => new NestedTilesetLoadedResult(command.Tile, nested.Tileset),
                    RenderableContentProcessResult renderable => await ExecuteRenderableContentAsync(command.Tile, renderable, request, interactiveContext, cancellationToken).ConfigureAwait(false),
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
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<PlacedMeshPayload> placedMeshes = _meshPlacementService.Place(
                tile,
                renderable.Meshes,
                request.PlacementReference,
                request.Output.MeshParentSlotId);

            if (request.Output.DryRun)
            {
                return new RenderableContentReadyResult(tile, placedMeshes.Count, [], renderable.AssetCopyright);
            }

            return await ExecuteStreamPlacedMeshesAsync(
                new StreamPlacedMeshesCommand(tile, placedMeshes, renderable.AssetCopyright),
                interactiveContext,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<PlannerResult> ExecuteStreamPlacedMeshesAsync(
            StreamPlacedMeshesCommand command,
            InteractiveExecutionContext? interactiveContext,
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
                        interactiveContext?.TrackNewSlotId(slotId);
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

        private async Task<PlannerResult> ExecuteRemoveSlotsAsync(
            RemoveSlotsCommand command,
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            int failedSlotCount = 0;
            Exception? firstError = null;
            IReadOnlyList<string> immediateSlotIds = command.SlotIds;

            if (interactiveContext is not null)
            {
                immediateSlotIds = interactiveContext.StageRetainedRemovals(command.TileId, command.SlotIds);
            }

            foreach (string slotId in immediateSlotIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                    interactiveContext?.ForgetNewSlotId(slotId);
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

        private async Task<HashSet<string>> CommitInteractiveChangesAsync(
            InteractiveExecutionContext interactiveContext,
            IReadOnlySet<string> selectedTileStableIds,
            CancellationToken cancellationToken)
        {
            var failedStateIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (RetainedTileRemoval stagedRemoval in interactiveContext.GetStagedRetainedRemovals())
            {
                if (!await TryRemoveSlotsAsync(stagedRemoval.TileId, stagedRemoval.SlotIds, cancellationToken).ConfigureAwait(false))
                {
                    _ = failedStateIds.Add(stagedRemoval.StateId);
                }
            }

            if (!interactiveContext.RemoveOutOfRangeTiles)
            {
                return failedStateIds;
            }

            foreach ((string stableId, RetainedTileState retainedTile) in interactiveContext.RetainedTiles)
            {
                if (selectedTileStableIds.Contains(stableId))
                {
                    continue;
                }

                if (!await TryRemoveSlotsAsync(retainedTile.TileId, retainedTile.SlotIds, cancellationToken).ConfigureAwait(false))
                {
                    _ = failedStateIds.Add(stableId);
                }
            }

            return failedStateIds;
        }

        private async Task RollbackInteractiveChangesAsync(
            TileRunRequest request,
            InteractiveExecutionContext interactiveContext)
        {
            foreach (string slotId in interactiveContext.GetNewSlotIds())
            {
                try
                {
                    await _resoniteSession.RemoveSlotAsync(slotId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to rollback newly streamed slot {SlotId}.", slotId);
                }
            }

            await ApplyFinalLicenseCreditAsync(request, interactiveContext.RetainedTiles, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<bool> TryRemoveSlotsAsync(
            string tileId,
            IReadOnlyList<string> slotIds,
            CancellationToken cancellationToken)
        {
            foreach (string slotId in slotIds)
            {
                try
                {
                    await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove retained slot {SlotId} for tile {TileId}.", slotId, tileId);
                    return false;
                }
            }

            return true;
        }

        private async Task ApplyFinalLicenseCreditAsync(
            TileRunRequest request,
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            CancellationToken cancellationToken)
        {
            if (request.Output.DryRun)
            {
                return;
            }

            var aggregator = new LicenseCreditAggregator();
            foreach (RetainedTileState retainedTile in retainedTiles.Values)
            {
                IReadOnlyList<string> owners = LicenseCreditAggregator.ParseOwners(
                    string.IsNullOrWhiteSpace(retainedTile.AssetCopyright)
                        ? []
                        : [retainedTile.AssetCopyright]);
                aggregator.RegisterOrder(owners);
                _ = aggregator.Activate(owners);
            }

            await _resoniteSession.SetSessionLicenseCreditAsync(aggregator.BuildCreditString(), cancellationToken).ConfigureAwait(false);
        }

        private static IReadOnlyDictionary<string, RetainedTileState> BuildNextRetainedTiles(
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            IReadOnlyDictionary<string, RetainedTileState> visibleTiles,
            IReadOnlySet<string> selectedTileStableIds,
            bool removeOutOfRangeTiles,
            IReadOnlySet<string> failedRemovalStateIds)
        {
            var next = new Dictionary<string, RetainedTileState>(visibleTiles, StringComparer.Ordinal);

            foreach ((string stableId, RetainedTileState retainedTile) in retainedTiles)
            {
                if (next.ContainsKey(stableId))
                {
                    continue;
                }

                if (failedRemovalStateIds.Contains(stableId))
                {
                    next[stableId] = retainedTile;
                    continue;
                }

                if (selectedTileStableIds.Contains(stableId) || removeOutOfRangeTiles)
                {
                    continue;
                }

                next[stableId] = retainedTile;
            }

            return next;
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

        private async Task SetProgressAsync(TileRunRequest request, PlannerProgress progress, CancellationToken cancellationToken)
        {
            if (request.Output.DryRun)
            {
                return;
            }

            await _resoniteSession.SetProgressAsync(
                request.Output.MeshParentSlotId,
                BuildProgressValue(progress),
                BuildProgressText(progress),
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

        private static float BuildProgressValue(PlannerProgress progress)
        {
            int completedUnits = progress.ProcessedTiles + progress.FailedTiles;
            int pendingUnits =
                progress.PendingTilesets +
                progress.PendingGlbTiles +
                progress.DeferredGlbTiles +
                progress.PendingTileCommands;
            int totalUnits = completedUnits + pendingUnits;

            if (totalUnits <= 0)
            {
                return 0f;
            }

            return System.Math.Clamp((float)completedUnits / totalUnits, 0f, 1f);
        }

        private static string BuildProgressText(RunSummary summary, bool completed)
        {
            string prefix = completed ? "Completed" : "Running";
            return $"{prefix}: candidate={summary.CandidateTiles} processed={summary.ProcessedTiles} streamed={summary.StreamedMeshes} failed={summary.FailedTiles}";
        }

        private static string BuildProgressText(PlannerProgress progress)
        {
            return $"Running: candidate={progress.CandidateTiles} processed={progress.ProcessedTiles} streamed={progress.StreamedMeshes} failed={progress.FailedTiles}";
        }

        private static string ResolveStableId(TileSelectionResult tile)
        {
            return string.IsNullOrWhiteSpace(tile.StableId)
                ? tile.TileId
                : tile.StableId!;
        }

        private sealed class InteractiveExecutionContext
        {
            private readonly Dictionary<string, RetainedTileState> _retainedTiles = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _stableIdByRetainedSlotId = new(StringComparer.Ordinal);
            private readonly Dictionary<string, RetainedTileRemoval> _stagedRetainedRemovals = new(StringComparer.Ordinal);
            private readonly HashSet<string> _newSlotIds = new(StringComparer.Ordinal);

            public InteractiveExecutionContext(
                IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
                bool removeOutOfRangeTiles)
            {
                RemoveOutOfRangeTiles = removeOutOfRangeTiles;
                foreach ((string stableId, RetainedTileState retainedTile) in retainedTiles)
                {
                    _retainedTiles[stableId] = retainedTile;
                    foreach (string slotId in retainedTile.SlotIds)
                    {
                        _stableIdByRetainedSlotId[slotId] = stableId;
                    }
                }
            }

            public IReadOnlyDictionary<string, RetainedTileState> RetainedTiles => _retainedTiles;

            public bool RemoveOutOfRangeTiles { get; }

            public bool TryGetRetainedTile(TileSelectionResult tile, out RetainedTileState retainedTile)
            {
                if (_retainedTiles.TryGetValue(ResolveStableId(tile), out RetainedTileState? existing))
                {
                    retainedTile = existing;
                    return true;
                }

                retainedTile = null!;
                return false;
            }

            public void TrackNewSlotId(string slotId)
            {
                if (!string.IsNullOrWhiteSpace(slotId))
                {
                    _ = _newSlotIds.Add(slotId);
                }
            }

            public void ForgetNewSlotId(string slotId)
            {
                if (!string.IsNullOrWhiteSpace(slotId))
                {
                    _ = _newSlotIds.Remove(slotId);
                }
            }

            public IReadOnlyList<string> GetNewSlotIds()
            {
                return _newSlotIds.ToArray();
            }

            public IReadOnlyList<string> StageRetainedRemovals(string tileId, IReadOnlyList<string> slotIds)
            {
                var immediateSlotIds = new List<string>(slotIds.Count);

                foreach (string slotId in slotIds)
                {
                    if (!_stableIdByRetainedSlotId.TryGetValue(slotId, out string? stableId) ||
                        !_retainedTiles.TryGetValue(stableId, out RetainedTileState? retainedTile))
                    {
                        immediateSlotIds.Add(slotId);
                        continue;
                    }

                    if (!_stagedRetainedRemovals.TryGetValue(stableId, out RetainedTileRemoval? removal))
                    {
                        removal = new RetainedTileRemoval(stableId, tileId, retainedTile);
                        _stagedRetainedRemovals[stableId] = removal;
                    }

                    removal.AddSlot(slotId);
                }

                return immediateSlotIds;
            }

            public IReadOnlyCollection<RetainedTileRemoval> GetStagedRetainedRemovals()
            {
                return _stagedRetainedRemovals.Values.ToArray();
            }
        }

        private sealed class RetainedTileRemoval(string stateId, string tileId, RetainedTileState retainedTile)
        {
            private readonly HashSet<string> _slotIds = new(StringComparer.Ordinal);

            public string StateId { get; } = stateId;

            public string TileId { get; } = tileId;

            public RetainedTileState RetainedTile { get; } = retainedTile;

            public IReadOnlyList<string> SlotIds => _slotIds.ToArray();

            public void AddSlot(string slotId)
            {
                if (!string.IsNullOrWhiteSpace(slotId))
                {
                    _ = _slotIds.Add(slotId);
                }
            }
        }
    }
}
