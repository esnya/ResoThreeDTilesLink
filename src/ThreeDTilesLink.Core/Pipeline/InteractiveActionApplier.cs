using System.Net.WebSockets;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class InteractiveActionApplier(
        ITileSelectionService tileRunCoordinator,
        IResoniteSession resoniteSession,
        IWatchStore watchStore,
        ISearchResolver searchResolver,
        ICoordinateTransformer coordinateTransformer,
        ILogger<InteractiveRunSupervisor> logger)
    {
        private static readonly Action<ILogger, string, string, Exception?> s_retainedSlotClearFailed =
            LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(18, "RetainedSlotClearFailed"),
                "Failed to clear retained slot {SlotId} for tile {TileId} before non-overlap interactive rerun.");

        private readonly ITileSelectionService _tileRunCoordinator = tileRunCoordinator;
        private readonly IResoniteSession _resoniteSession = resoniteSession;
        private readonly IWatchStore _watchStore = watchStore;
        private readonly ISearchResolver _searchResolver = searchResolver;
        private readonly ICoordinateTransformer _coordinateTransformer = coordinateTransformer;
        private readonly ILogger<InteractiveRunSupervisor> _logger = logger;

        internal Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            return _resoniteSession.ConnectAsync(host, port, cancellationToken);
        }

        internal Vector3d GeographicToEcef(double latitude, double longitude, double heightM)
        {
            return _coordinateTransformer.GeographicToEcef(latitude, longitude, heightM);
        }

        internal Vector3d EcefToEnu(Vector3d ecef, GeoReference reference)
        {
            return _coordinateTransformer.EcefToEnu(ecef, reference);
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

        internal async Task<InteractiveLoopState> ApplyAsync(
            InteractiveLoopState state,
            IEnumerable<InteractiveAction> actions,
            InteractiveRunRequest options,
            CancellationToken cancellationToken)
        {
            foreach (InteractiveAction action in actions)
            {
                state = action switch
                {
                    ResolveSearchAction resolve => await ResolveSearchAsync(state, options, resolve, cancellationToken).ConfigureAwait(false),
                    CancelActiveRunAction => await CancelActiveRunAsync(state).ConfigureAwait(false),
                    StartRunAction start => await StartRunAsync(state, options, start, cancellationToken).ConfigureAwait(false),
                    _ => state
                };
            }

            return state;
        }

        internal async Task<InteractiveLoopState> DisconnectAsync(
            InteractiveLoopState state,
            CancellationToken _)
        {
            state = await CancelActiveRunAsync(state).ConfigureAwait(false);

            if (state.Connected)
            {
                Task disconnectTask = _resoniteSession.DisconnectAsync(CancellationToken.None);
                await ObserveCompletionAsync(disconnectTask).ConfigureAwait(false);

                if (TryGetNonCancellationFailure(disconnectTask) is { } disconnectFailure)
                {
                    InteractiveRunSupervisor.Log.DisconnectFailed(_logger, disconnectFailure);
                }
            }

            return state with { ActiveRun = null, Connected = false };
        }

        private async Task<InteractiveLoopState> ResolveSearchAsync(
            InteractiveLoopState state,
            InteractiveRunRequest options,
            ResolveSearchAction action,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                InteractiveRunSupervisor.Log.SearchIgnored(_logger, action.SearchText);
                return state;
            }

            try
            {
                LocationSearchResult? result = await _searchResolver.SearchAsync(options.ApiKey, action.SearchText, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    InteractiveRunSupervisor.Log.SearchNoResult(_logger, action.SearchText);
                    return state;
                }

                await _watchStore.UpdateWatchCoordinatesAsync(state.WatchBinding!, result.Latitude, result.Longitude, cancellationToken).ConfigureAwait(false);
                InteractiveRunSupervisor.Log.SearchResolved(
                    _logger,
                    action.SearchText,
                    result.FormattedAddress,
                    result.Latitude,
                    result.Longitude);
                return state with
                {
                    AwaitingResolvedCoordinates = result,
                    PendingValues = null,
                    PendingValuesChangedAt = null,
                    LastResolvedSearch = action.SearchText
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ArgumentException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
            catch (HttpRequestException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
            catch (JsonException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
            catch (TimeoutException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
            catch (NotSupportedException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
            catch (ObjectDisposedException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
            catch (WebSocketException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
            catch (UriFormatException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
            catch (InvalidOperationException ex)
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return state;
            }
        }

        private async Task<InteractiveLoopState> CancelActiveRunAsync(InteractiveLoopState state)
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

        [SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The linked cancellation source is owned by InteractiveActiveRun and disposed when the run completes or is superseded.")]
        private async Task<InteractiveLoopState> StartRunAsync(
            InteractiveLoopState state,
            InteractiveRunRequest options,
            StartRunAction action,
            CancellationToken cancellationToken)
        {
            GeoReference selectionReference = new(action.Values.Latitude, action.Values.Longitude, options.HeightOffsetM);
            GeoReference placementReference = action.Overlaps && state.PlacementReference is not null
                ? state.PlacementReference
                : selectionReference;
            if (!action.Overlaps)
            {
                await ClearRetainedTilesAsync(state.RetainedTiles, cancellationToken).ConfigureAwait(false);
            }

            Dictionary<string, RetainedTileState> retainedTiles = action.Overlaps
                ? state.RetainedTiles
                : new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);
            InteractiveRunCheckpoint? checkpoint = action.Overlaps ? state.Checkpoint : null;
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
                    checkpoint),
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
                Checkpoint = checkpoint,
                ActiveRun = new InteractiveActiveRun(activeRunTask, activeRunCts)
            };
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Non-overlap interactive reruns should continue even if some retained slots fail to clear.")]
        private async Task ClearRetainedTilesAsync(
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            CancellationToken cancellationToken)
        {
            foreach (RetainedTileState retainedTile in retainedTiles.Values)
            {
                foreach (string slotId in retainedTile.SlotIds)
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
                        s_retainedSlotClearFailed(_logger, slotId, retainedTile.TileId, ex);
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
                    options.Traversal.MaxTiles,
                    options.Traversal.MaxDepth,
                    options.Traversal.DetailTargetM,
                    options.Traversal.BootstrapRangeMultiplier),
                new ResoniteOutputOptions(
                    options.ResoniteHost,
                    options.ResonitePort,
                    options.DryRun,
                    ManageConnection: false),
                options.ApiKey);
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
