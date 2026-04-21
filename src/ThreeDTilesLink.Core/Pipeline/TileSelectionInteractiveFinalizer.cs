using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TileSelectionInteractiveFinalizer(
        IResoniteSession resoniteSession,
        IResoniteSessionMetadataPort sessionMetadataPort,
        ILicenseCreditPolicy licenseCreditPolicy,
        ILogger logger)
    {
        private readonly IResoniteSession _resoniteSession = resoniteSession;
        private readonly IResoniteSessionMetadataPort _sessionMetadataPort = sessionMetadataPort;
        private readonly ILicenseCreditPolicy _licenseCreditPolicy = licenseCreditPolicy;
        private readonly ILogger _logger = logger;

        private static readonly Action<ILogger, string, Exception?> s_rollbackStreamedSlotFailed =
            LoggerMessage.Define<string>(
                LogLevel.Warning,
                new EventId(16, "RollbackStreamedSlotFailed"),
                "Failed to rollback newly streamed slot {SlotId}.");

        private static readonly Action<ILogger, string, string, Exception?> s_removeRetainedSlotFailed =
            LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(17, "RemoveRetainedSlotFailed"),
                "Failed to remove retained slot {SlotId} for tile {TileId}.");

        public async Task<IReadOnlyDictionary<string, RetainedTileState>> CommitInteractiveChangesAsync(
            TileSelectionInteractiveExecutionContext interactiveContext,
            IReadOnlySet<string> selectedTileStableIds,
            CancellationToken cancellationToken)
        {
            var failedRetainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);

            if (!interactiveContext.RemoveOutOfRangeTiles)
            {
                return failedRetainedTiles;
            }

            foreach ((string stableId, RetainedTileState retainedTile) in interactiveContext.RetainedTiles)
            {
                if (selectedTileStableIds.Contains(stableId))
                {
                    continue;
                }

                IReadOnlyList<string> failedSlotIds = await TryRemoveSlotsAsync(
                    retainedTile.TileId,
                    retainedTile.SlotIds,
                    cancellationToken).ConfigureAwait(false);
                if (failedSlotIds.Count > 0)
                {
                    failedRetainedTiles[stableId] = retainedTile with
                    {
                        SlotIds = failedSlotIds
                    };
                }
            }

            return failedRetainedTiles;
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Interactive rollback is intentionally tolerant of transient session failures.")]
        public async Task RollbackInteractiveChangesAsync(
            TileRunRequest request,
            TileSelectionInteractiveExecutionContext interactiveContext)
        {
            foreach (string slotId in interactiveContext.GetNewSlotIds())
            {
                try
                {
                    await _resoniteSession.RemoveSlotAsync(slotId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    s_rollbackStreamedSlotFailed(_logger, slotId, ex);
                }
            }

            await ApplyFinalLicenseCreditAsync(request, interactiveContext.RetainedTiles, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<InteractiveTileRunResult> FinalizeCanceledInteractiveRunAsync(
            TileRunRequest request,
            InteractiveRunInput interactive,
            TileSelectionInteractiveExecutionContext interactiveContext,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, RetainedTileState> nextRetainedTiles =
                new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal);
            IReadOnlyDictionary<string, RetainedTileState> nextCleanupDebtTiles =
                new Dictionary<string, RetainedTileState>(interactive.CleanupDebtTiles, StringComparer.Ordinal);
            IReadOnlySet<string> selectedTileStableIds = new HashSet<string>(StringComparer.Ordinal);
            InteractiveRunCheckpoint? checkpoint = interactive.Checkpoint;
            RunSummary summary = new(0, 0, 0, 0);

            if (interactiveContext.LastState is not null)
            {
                nextRetainedTiles = BuildCanceledRetainedTiles(interactive.RetainedTiles, interactiveContext.LastState.VisibleTiles);
                nextCleanupDebtTiles = new Dictionary<string, RetainedTileState>(interactiveContext.LastState.CleanupDebtTiles, StringComparer.Ordinal);
                selectedTileStableIds = interactiveContext.LastState.SelectedTileStableIds;
                checkpoint = interactiveContext.LastState.Checkpoint;
                summary = interactiveContext.LastState.Summary;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await ApplyFinalLicenseCreditAsync(request, nextRetainedTiles, cancellationToken).ConfigureAwait(false);
            }

            return new InteractiveTileRunResult(summary, nextRetainedTiles, selectedTileStableIds, checkpoint, nextCleanupDebtTiles);
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Best-effort retained slot removal captures failures for caller to handle.")]
        public async Task<IReadOnlyList<string>> TryRemoveSlotsAsync(
            string tileId,
            IReadOnlyList<string> slotIds,
            CancellationToken cancellationToken)
        {
            var failedSlotIds = new List<string>();

            foreach (string slotId in slotIds)
            {
                try
                {
                    await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    s_removeRetainedSlotFailed(_logger, slotId, tileId, ex);
                    failedSlotIds.Add(slotId);
                }
            }

            return failedSlotIds;
        }

        public async Task ApplyFinalLicenseCreditAsync(
            TileRunRequest request,
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            CancellationToken cancellationToken)
        {
            if (request.Output.DryRun)
            {
                return;
            }

            var aggregator = new LicenseCreditAggregator(_licenseCreditPolicy);
            foreach (RetainedTileState retainedTile in retainedTiles.Values)
            {
                IReadOnlyList<string> owners = aggregator.ParseOwners(
                    string.IsNullOrWhiteSpace(retainedTile.AssetCopyright)
                        ? []
                        : [retainedTile.AssetCopyright]);
                aggregator.RegisterOrder(owners);
                _ = aggregator.Activate(owners);
            }

            string built = aggregator.BuildCreditString();
            await _sessionMetadataPort.SetSessionLicenseCreditAsync(
                string.IsNullOrWhiteSpace(built) ? _licenseCreditPolicy.DefaultCredit : built,
                cancellationToken).ConfigureAwait(false);
        }

        public static Dictionary<string, RetainedTileState> BuildNextRetainedTiles(
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            IReadOnlyDictionary<string, RetainedTileState> visibleTiles,
            IReadOnlySet<string> selectedTileStableIds,
            bool removeOutOfRangeTiles,
            IReadOnlyDictionary<string, RetainedTileState> failedRetainedTiles)
        {
            var next = new Dictionary<string, RetainedTileState>(visibleTiles, StringComparer.Ordinal);

            foreach ((string stableId, RetainedTileState retainedTile) in retainedTiles)
            {
                if (next.ContainsKey(stableId))
                {
                    continue;
                }

                if (failedRetainedTiles.TryGetValue(stableId, out RetainedTileState? failedRetainedTile))
                {
                    next[stableId] = failedRetainedTile;
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

        private static Dictionary<string, RetainedTileState> BuildCanceledRetainedTiles(
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            IReadOnlyDictionary<string, RetainedTileState> visibleTiles)
        {
            return BuildNextRetainedTiles(
                retainedTiles,
                visibleTiles,
                new HashSet<string>(StringComparer.Ordinal),
                removeOutOfRangeTiles: false,
                new Dictionary<string, RetainedTileState>(StringComparer.Ordinal));
        }
    }
}
