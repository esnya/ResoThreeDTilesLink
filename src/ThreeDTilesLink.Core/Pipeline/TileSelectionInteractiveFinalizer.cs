using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TileSelectionInteractiveFinalizer(
        ISceneSession sceneSession,
        ISceneMetadataSink metadataSink,
        ILicenseCreditPolicy licenseCreditPolicy,
        ILogger logger)
    {
        private readonly ISceneSession _sceneSession = sceneSession;
        private readonly ISceneMetadataSink _metadataSink = metadataSink;
        private readonly ILicenseCreditPolicy _licenseCreditPolicy = licenseCreditPolicy;
        private readonly ILogger _logger = logger;

        private static readonly Action<ILogger, string, Exception?> s_rollbackStreamedNodeFailed =
            LoggerMessage.Define<string>(
                LogLevel.Warning,
                new EventId(16, "RollbackStreamedNodeFailed"),
                "Failed to rollback newly streamed node {NodeId}.");

        private static readonly Action<ILogger, string, string, Exception?> s_removeRetainedNodeFailed =
            LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(17, "RemoveRetainedNodeFailed"),
                "Failed to remove retained node {NodeId} for tile {TileId}.");

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

                IReadOnlyList<string> failedNodeIds = await TryRemoveNodesAsync(
                    retainedTile.TileId,
                    retainedTile.NodeIds,
                    cancellationToken).ConfigureAwait(false);
                if (failedNodeIds.Count > 0)
                {
                    failedRetainedTiles[stableId] = retainedTile with
                    {
                        NodeIds = failedNodeIds
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
            foreach (string nodeId in interactiveContext.GetNewNodeIds())
            {
                try
                {
                    await _sceneSession.RemoveNodeAsync(nodeId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    s_rollbackStreamedNodeFailed(_logger, nodeId, ex);
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
            Justification = "Best-effort retained node removal captures failures for caller to handle.")]
        public async Task<IReadOnlyList<string>> TryRemoveNodesAsync(
            string tileId,
            IReadOnlyList<string> nodeIds,
            CancellationToken cancellationToken)
        {
            var failedNodeIds = new List<string>();

            foreach (string nodeId in nodeIds)
            {
                try
                {
                    await _sceneSession.RemoveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    s_removeRetainedNodeFailed(_logger, nodeId, tileId, ex);
                    failedNodeIds.Add(nodeId);
                }
            }

            return failedNodeIds;
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
            await _metadataSink.SetSessionLicenseCreditAsync(
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
