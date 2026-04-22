using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class InteractiveSessionManager(
        ITileSelectionService tileRunCoordinator,
        ISceneSession sceneSession,
        ILogger<InteractiveSessionManager> logger)
    {
        private static readonly Action<ILogger, string, string, Exception?> s_retainedNodeClearFailed =
            LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(18, "RetainedNodeClearFailed"),
                "Failed to clear retained node {NodeId} for tile {TileId} before non-overlap interactive rerun.");

        private readonly ITileSelectionService _tileRunCoordinator = tileRunCoordinator;
        private readonly ISceneSession _sceneSession = sceneSession;
        private readonly ILogger<InteractiveSessionManager> _logger = logger;

        internal Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            return _sceneSession.ConnectAsync(host, port, cancellationToken);
        }

        internal async Task<InteractiveLoopState> FinalizeCompletedRunAsync(
            InteractiveLoopState state,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (state.ActiveRun is null || !state.ActiveRun.Task.IsCompleted)
            {
                return state;
            }

            if (state.ActiveRun.Task.IsCompletedSuccessfully)
            {
                InteractiveTileRunResult result = await state.ActiveRun.Task.ConfigureAwait(false);
                InteractiveRunSupervisor.Log.RunCompleted(
                    _logger,
                    result.VisibleTiles.Count,
                    result.Summary.CandidateTiles,
                    result.Summary.ProcessedTiles,
                    result.Summary.StreamedMeshes,
                    result.Summary.FailedTiles);
                state = state with
                {
                    RetainedTiles = new Dictionary<string, RetainedTileState>(result.VisibleTiles, StringComparer.Ordinal),
                    CleanupDebtTiles = new Dictionary<string, RetainedTileState>(result.CleanupDebtTiles, StringComparer.Ordinal),
                    Checkpoint = result.Checkpoint
                };
            }
            else if (TryGetNonCancellationFailure(state.ActiveRun.Task) is { } nonCancellationFailure)
            {
                InteractiveRunSupervisor.Log.RunFailed(_logger, nonCancellationFailure);
            }

            state.ActiveRun.CancellationSource.Dispose();
            return state with { ActiveRun = null };
        }

        internal async Task<InteractiveLoopState> CancelActiveRunAsync(InteractiveLoopState state)
        {
            if (state.ActiveRun is null)
            {
                return state;
            }

            if (!state.ActiveRun.Task.IsCompleted)
            {
                await state.ActiveRun.CancellationSource.CancelAsync().ConfigureAwait(false);
                await ObserveCompletionAsync(state.ActiveRun.Task).ConfigureAwait(false);
            }

            if (state.ActiveRun.Task.IsCompletedSuccessfully)
            {
                InteractiveTileRunResult result = await state.ActiveRun.Task.ConfigureAwait(false);
                state = state with
                {
                    RetainedTiles = new Dictionary<string, RetainedTileState>(result.VisibleTiles, StringComparer.Ordinal),
                    CleanupDebtTiles = new Dictionary<string, RetainedTileState>(result.CleanupDebtTiles, StringComparer.Ordinal),
                    Checkpoint = result.Checkpoint
                };
            }
            else if (TryGetNonCancellationFailure(state.ActiveRun.Task) is { } nonCancellationFailure)
            {
                InteractiveRunSupervisor.Log.RunSupersededFailed(_logger, nonCancellationFailure);
            }

            state.ActiveRun.CancellationSource.Dispose();
            return state with { ActiveRun = null };
        }

        internal async Task<InteractiveLoopState> DisconnectAsync(
            InteractiveLoopState state,
            CancellationToken cancellationToken)
        {
            state = await CancelActiveRunAsync(state).ConfigureAwait(false);

            if (state.Connected)
            {
                Task disconnectTask = _sceneSession.DisconnectAsync(cancellationToken);
                await ObserveCompletionAsync(disconnectTask).ConfigureAwait(false);

                if (TryGetNonCancellationFailure(disconnectTask) is { } disconnectFailure)
                {
                    InteractiveRunSupervisor.Log.DisconnectFailed(_logger, disconnectFailure);
                }
            }

            return state with { ActiveRun = null, Connected = false };
        }

        [SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The linked cancellation source is owned by InteractiveActiveRun and disposed when the run completes or is superseded.")]
        internal async Task<InteractiveLoopState> StartRunAsync(
            InteractiveLoopState state,
            InteractiveRunRequest options,
            StartRunAction action,
            CancellationToken cancellationToken)
        {
            GeoReference selectionReference = action.SelectionReference;
            GeoReference placementReference = action.Overlaps && state.PlacementReference is not null
                ? state.PlacementReference
                : selectionReference;
            if (!action.Overlaps)
            {
                await ClearRetainedTilesAsync(state.RetainedTiles, state.CleanupDebtTiles, cancellationToken).ConfigureAwait(false);
            }

            Dictionary<string, RetainedTileState> retainedTiles = action.Overlaps
                ? state.RetainedTiles
                : new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);
            Dictionary<string, RetainedTileState> cleanupDebtTiles = action.Overlaps
                ? state.CleanupDebtTiles
                : new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);
            InteractiveRunCheckpoint? checkpoint = action.ReuseCheckpoint ? state.Checkpoint : null;
            TileRunRequest runRequest = BuildRunRequest(
                options,
                selectionReference,
                placementReference,
                action.Values.RangeM);

            var activeRunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<InteractiveTileRunResult> activeRunTask = _tileRunCoordinator.RunInteractiveAsync(
                runRequest,
                new InteractiveRunInput(
                    retainedTiles,
                    action.Overlaps && options.RemoveOutOfRange,
                    checkpoint,
                    cleanupDebtTiles),
                activeRunCts.Token);

            InteractiveRunSupervisor.Log.RunStarted(
                _logger,
                action.Values.Latitude,
                action.Values.Longitude,
                placementReference.Latitude,
                placementReference.Longitude,
                action.Values.RangeM,
                action.Overlaps);

            return state with
            {
                PlacementReference = placementReference,
                RetainedTiles = retainedTiles,
                CleanupDebtTiles = cleanupDebtTiles,
                Checkpoint = checkpoint,
                ActiveRun = new InteractiveActiveRun(activeRunTask, activeRunCts)
            };
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Non-overlap interactive reruns should continue even if some retained nodes fail to clear.")]
        private async Task ClearRetainedTilesAsync(
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            IReadOnlyDictionary<string, RetainedTileState> cleanupDebtTiles,
            CancellationToken cancellationToken)
        {
            foreach (RetainedTileState retainedTile in retainedTiles.Values.Concat(cleanupDebtTiles.Values))
            {
                foreach (string nodeId in retainedTile.NodeIds)
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
                        s_retainedNodeClearFailed(_logger, nodeId, retainedTile.TileId, ex);
                    }
                }
            }
        }

        private static TileRunRequest BuildRunRequest(
            InteractiveRunRequest options,
            GeoReference selectionReference,
            GeoReference placementReference,
            double rangeM)
        {
            return new TileRunRequest(
                selectionReference,
                placementReference,
                new TraversalOptions(
                    rangeM,
                    options.Traversal.DetailTargetM,
                    options.Traversal.BootstrapRangeMultiplier),
                new SceneOutputOptions(
                    options.EndpointHost,
                    options.EndpointPort,
                    false,
                    ManageConnection: false),
                options.TileSource);
        }

        private static Exception? TryGetNonCancellationFailure(Task task)
        {
            if (task.Exception is null)
            {
                return null;
            }

            foreach (Exception exception in task.Exception.Flatten().InnerExceptions)
            {
                if (exception is not OperationCanceledException)
                {
                    return exception;
                }
            }

            return null;
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Cleanup paths intentionally swallow task failures and inspect them separately.")]
        private static async Task ObserveCompletionAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}
