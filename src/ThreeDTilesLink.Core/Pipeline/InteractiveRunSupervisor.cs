using System.Globalization;
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
    /// <summary>
    /// Coordinates interactive tile runs driven by probe input and search requests.
    /// </summary>
    /// <param name="tileRunCoordinator">The tile-run coordinator used to execute runs.</param>
    /// <param name="resoniteSession">Resonite session operations for slot management.</param>
    /// <param name="probeStore">Probe state accessor.</param>
    /// <param name="searchResolver">Optional search resolver for geographic queries.</param>
    /// <param name="coordinateTransformer">Coordinate utility used to compare footprints.</param>
    /// <param name="clock">Clock abstraction for testability and cancellation-aware timing.</param>
    /// <param name="probeMonitor">Monitor helper for probe reads.</param>
    /// <param name="logger">Logger.</param>
    internal sealed partial class InteractiveRunSupervisor(
        ITileRunCoordinator tileRunCoordinator,
        IResoniteSession resoniteSession,
        IProbeStore probeStore,
        ISearchResolver searchResolver,
        ICoordinateTransformer coordinateTransformer,
        IClock clock,
        ProbeMonitor probeMonitor,
        ILogger<InteractiveRunSupervisor> logger)
    {
        private readonly ITileRunCoordinator _tileRunCoordinator = tileRunCoordinator;
        private readonly IResoniteSession _resoniteSession = resoniteSession;
        private readonly IProbeStore _probeStore = probeStore;
        private readonly ISearchResolver _searchResolver = searchResolver;
        private readonly ICoordinateTransformer _coordinateTransformer = coordinateTransformer;
        private readonly IClock _clock = clock;
        private readonly ProbeMonitor _probeMonitor = probeMonitor;
        private readonly ILogger<InteractiveRunSupervisor> _logger = logger;

        /// <summary>
        /// Runs the interactive loop, launching and superseding tile runs based on live probe updates.
        /// </summary>
        /// <param name="options">Interactive run options.</param>
        /// <param name="cancellationToken">Token that signals when interactive mode should stop.</param>
        /// <returns>Task that completes when the interactive session ends.</returns>
        public async Task RunAsync(InteractiveRunRequest options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ProbeMonitor.ValidateIntervals(options.ProbeWatch);

            string? sessionSlotId = null;
            GeoReference? placementReference = null;
            RangeFootprint? lastRequestedFootprint = null;
            Task<InteractiveTileRunResult>? activeRunTask = null;
            CancellationTokenSource? activeRunCts = null;
            Dictionary<string, RetainedTileState> retainedTiles = new(StringComparer.Ordinal);
            InteractiveRunCheckpoint? retainedCheckpoint = null;
            ProbeBinding? probeBinding = null;
            ProbeValues? lastProbeValues = null;
            ProbeValues? pendingProbeValues = null;
            DateTimeOffset? pendingChangedAt = null;
            string? lastObservedSearch = null;
            string? lastResolvedSearch = null;
            string? pendingSearch = null;
            DateTimeOffset? pendingSearchChangedAt = null;
            LocationSearchResult? awaitingSearchCoordinates = null;
            DateTimeOffset lastRunStartedAt = DateTimeOffset.MinValue;
            bool connected = false;

            try
            {
                Log.ConnectingToResonite(_logger, options.ResoniteHost, options.ResonitePort);
                await _resoniteSession.ConnectAsync(options.ResoniteHost, options.ResonitePort, cancellationToken).ConfigureAwait(false);
                connected = true;

                probeBinding = await _probeStore.CreateProbeAsync(options.ProbeWatch.Probe, cancellationToken).ConfigureAwait(false);
                Log.ProbeBindingAttached(
                    _logger,
                    probeBinding.SlotId,
                    probeBinding.OwnsSlot,
                    options.ProbeWatch.Probe.LatitudeVariablePath,
                    options.ProbeWatch.Probe.LongitudeVariablePath,
                    options.ProbeWatch.Probe.RangeVariablePath,
                    options.ProbeWatch.Probe.SearchVariablePath);

                while (!cancellationToken.IsCancellationRequested)
                {
                    (activeRunTask, activeRunCts, retainedTiles, retainedCheckpoint) = await ObserveActiveRunAsync(
                        activeRunTask,
                        activeRunCts,
                        retainedTiles,
                        retainedCheckpoint,
                        cancellationToken).ConfigureAwait(false);

                    string? currentSearch = await _probeMonitor.TryReadProbeSearchAsync(probeBinding, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(lastObservedSearch, currentSearch, StringComparison.Ordinal))
                    {
                        lastObservedSearch = currentSearch;
                        if (currentSearch is null)
                        {
                            pendingSearch = null;
                            pendingSearchChangedAt = null;
                            lastResolvedSearch = null;
                            awaitingSearchCoordinates = null;
                        }
                        else
                        {
                            pendingSearch = currentSearch;
                            pendingSearchChangedAt = _clock.UtcNow;
                            Log.SearchQueryChanged(_logger, currentSearch);
                        }
                    }

                    if (pendingSearch is not null &&
                        pendingSearchChangedAt is not null &&
                        _clock.UtcNow - pendingSearchChangedAt.Value >= options.ProbeWatch.Debounce &&
                        !string.Equals(lastResolvedSearch, pendingSearch, StringComparison.Ordinal))
                    {
                        LocationSearchResult? resolved = await ResolveSearchAsync(options, probeBinding, pendingSearch, cancellationToken).ConfigureAwait(false);
                        if (resolved is not null)
                        {
                            awaitingSearchCoordinates = resolved;
                            pendingProbeValues = null;
                            pendingChangedAt = null;
                            lastResolvedSearch = pendingSearch;
                        }

                        pendingSearch = null;
                        pendingSearchChangedAt = null;
                    }

                    ProbeValues? currentProbe = await _probeMonitor.TryReadProbeValuesAsync(probeBinding, cancellationToken).ConfigureAwait(false);
                    if (currentProbe is not null && awaitingSearchCoordinates is not null)
                    {
                        if (MatchesResolvedCoordinates(currentProbe, awaitingSearchCoordinates))
                        {
                            awaitingSearchCoordinates = null;
                        }
                        else
                        {
                            currentProbe = null;
                        }
                    }

                    if (currentProbe is not null && ProbeMonitor.HasMeaningfulChange(lastProbeValues, currentProbe))
                        {
                            pendingProbeValues = currentProbe;
                            pendingChangedAt = _clock.UtcNow;
                            lastProbeValues = currentProbe;
                            Log.ProbeChanged(
                                _logger,
                                currentProbe.Latitude,
                                currentProbe.Longitude,
                                currentProbe.RangeM);
                        }

                    if (pendingProbeValues is not null && pendingChangedAt is not null)
                    {
                        DateTimeOffset now = _clock.UtcNow;
                        bool debounceElapsed = now - pendingChangedAt.Value >= options.ProbeWatch.Debounce;
                        bool throttleElapsed = lastRunStartedAt == DateTimeOffset.MinValue || now - lastRunStartedAt >= options.ProbeWatch.Throttle;

                        if (debounceElapsed && throttleElapsed)
                        {
                            GeoReference selectionReference = new(
                                pendingProbeValues.Latitude,
                                pendingProbeValues.Longitude,
                                options.HeightOffsetM);
                            var currentFootprint = new RangeFootprint(selectionReference, pendingProbeValues.RangeM);
                            bool overlaps = lastRequestedFootprint is not null &&
                                Overlaps(lastRequestedFootprint, currentFootprint);

                            (activeRunTask, activeRunCts, retainedTiles, retainedCheckpoint) = await CancelActiveRunAsync(
                                activeRunTask,
                                activeRunCts,
                                retainedTiles,
                                retainedCheckpoint).ConfigureAwait(false);

                            if (!overlaps || string.IsNullOrWhiteSpace(sessionSlotId) || placementReference is null)
                            {
                                if (!string.IsNullOrWhiteSpace(sessionSlotId))
                                {
                                    await TryRemoveSlotAsync(sessionSlotId, cancellationToken).ConfigureAwait(false);
                                }

                                sessionSlotId = await _resoniteSession.CreateSessionChildSlotAsync(
                                    BuildRunSlotName(pendingProbeValues),
                                    cancellationToken).ConfigureAwait(false);
                                placementReference = selectionReference;
                                retainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);
                                retainedCheckpoint = null;
                            }

                            TileRunRequest runRequest = BuildRunRequest(
                                options,
                                selectionReference,
                                placementReference,
                                pendingProbeValues.RangeM,
                                sessionSlotId);
                            activeRunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            activeRunTask = _tileRunCoordinator.RunInteractiveAsync(
                                runRequest,
                                new InteractiveRunInput(
                                    retainedTiles,
                                    overlaps && options.RemoveOutOfRange,
                                    overlaps ? retainedCheckpoint : null),
                                activeRunCts.Token);
                            lastRequestedFootprint = currentFootprint;
                            lastRunStartedAt = now;
                            Log.RunStarted(
                                _logger,
                                sessionSlotId,
                                pendingProbeValues.Latitude,
                                pendingProbeValues.Longitude,
                                placementReference.Latitude,
                                placementReference.Longitude,
                                pendingProbeValues.RangeM,
                                overlaps);

                            pendingProbeValues = null;
                            pendingChangedAt = null;
                        }
                    }

                    await _clock.Delay(options.ProbeWatch.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                (activeRunTask, activeRunCts, retainedTiles, retainedCheckpoint) = await CancelActiveRunAsync(
                    activeRunTask,
                    activeRunCts,
                    retainedTiles,
                    retainedCheckpoint).ConfigureAwait(false);

                _ = activeRunTask;
                _ = activeRunCts;
                _ = retainedTiles;
                _ = retainedCheckpoint;

                if (!string.IsNullOrWhiteSpace(sessionSlotId) && !cancellationToken.IsCancellationRequested)
                {
                    await TryRemoveSlotAsync(sessionSlotId, CancellationToken.None).ConfigureAwait(false);
                }

                if (probeBinding is not null && probeBinding.OwnsSlot && !string.IsNullOrWhiteSpace(probeBinding.SlotId))
                {
                    await TryRemoveSlotAsync(probeBinding.SlotId, CancellationToken.None).ConfigureAwait(false);
                }

                if (connected)
                {
                    await TryDisconnectResoniteAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task<LocationSearchResult?> ResolveSearchAsync(
            InteractiveRunRequest options,
            ProbeBinding probeBinding,
            string searchText,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                Log.SearchIgnored(_logger, searchText);
                return null;
            }

            try
            {
                LocationSearchResult? result = await _searchResolver.SearchAsync(options.ApiKey, searchText, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    Log.SearchNoResult(_logger, searchText);
                    return null;
                }

                await _probeStore.UpdateProbeCoordinatesAsync(probeBinding, result.Latitude, result.Longitude, cancellationToken).ConfigureAwait(false);
                Log.SearchResolved(
                    _logger,
                    searchText,
                    result.FormattedAddress,
                    result.Latitude,
                    result.Longitude);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ArgumentException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
            catch (HttpRequestException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
            catch (JsonException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
            catch (TimeoutException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
            catch (NotSupportedException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
            catch (ObjectDisposedException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
            catch (WebSocketException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
            catch (UriFormatException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                Log.SearchResolutionFailed(_logger, ex, searchText);
                return null;
            }
        }

        private static bool MatchesResolvedCoordinates(ProbeValues probeValues, LocationSearchResult resolved)
        {
            const float coordinateTolerance = 1e-5f;
            return MathF.Abs(probeValues.Latitude - (float)resolved.Latitude) <= coordinateTolerance &&
                   MathF.Abs(probeValues.Longitude - (float)resolved.Longitude) <= coordinateTolerance;
        }

        private async Task<(Task<InteractiveTileRunResult>? Task, CancellationTokenSource? Cts, Dictionary<string, RetainedTileState> RetainedTiles, InteractiveRunCheckpoint? Checkpoint)> ObserveActiveRunAsync(
            Task<InteractiveTileRunResult>? activeRunTask,
            CancellationTokenSource? activeRunCts,
            Dictionary<string, RetainedTileState> retainedTiles,
            InteractiveRunCheckpoint? checkpoint,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (activeRunTask is null || !activeRunTask.IsCompleted)
            {
                return (activeRunTask, activeRunCts, retainedTiles, checkpoint);
            }

            if (activeRunTask.IsCompletedSuccessfully)
            {
                InteractiveTileRunResult result = await activeRunTask.ConfigureAwait(false);
                retainedTiles = new Dictionary<string, RetainedTileState>(result.VisibleTiles, StringComparer.Ordinal);
                checkpoint = result.Checkpoint;
                Log.RunCompleted(
                    _logger,
                    retainedTiles.Count,
                    result.Summary.CandidateTiles,
                    result.Summary.ProcessedTiles,
                    result.Summary.StreamedMeshes,
                    result.Summary.FailedTiles);
            }
            else if (TryGetNonCancellationFailure(activeRunTask) is { } nonCancellationFailure)
            {
                Log.RunFailed(_logger, nonCancellationFailure);
            }

            activeRunCts?.Dispose();

            return (null, null, retainedTiles, checkpoint);
        }

        private async Task<(Task<InteractiveTileRunResult>? Task, CancellationTokenSource? Cts, Dictionary<string, RetainedTileState> RetainedTiles, InteractiveRunCheckpoint? Checkpoint)> CancelActiveRunAsync(
            Task<InteractiveTileRunResult>? activeRunTask,
            CancellationTokenSource? activeRunCts,
            Dictionary<string, RetainedTileState> retainedTiles,
            InteractiveRunCheckpoint? checkpoint)
        {
            if (activeRunTask is null)
            {
                return (null, null, retainedTiles, checkpoint);
            }

            if (!activeRunTask.IsCompleted)
            {
                if (activeRunCts is not null)
                {
                    await activeRunCts.CancelAsync().ConfigureAwait(false);
                }

                await ObserveCompletionAsync(activeRunTask).ConfigureAwait(false);
            }

            if (activeRunTask.IsCompletedSuccessfully)
            {
                InteractiveTileRunResult result = await activeRunTask.ConfigureAwait(false);
                retainedTiles = new Dictionary<string, RetainedTileState>(result.VisibleTiles, StringComparer.Ordinal);
                checkpoint = result.Checkpoint;
            }
            else if (TryGetNonCancellationFailure(activeRunTask) is { } nonCancellationFailure)
            {
                Log.RunSupersededFailed(_logger, nonCancellationFailure);
            }

            activeRunCts?.Dispose();

            return (null, null, retainedTiles, checkpoint);
        }

        private bool Overlaps(RangeFootprint previous, RangeFootprint current)
        {
            Vector3d currentEcef = _coordinateTransformer.GeographicToEcef(
                current.Reference.Latitude,
                current.Reference.Longitude,
                current.Reference.HeightM);
            Vector3d currentEnu = _coordinateTransformer.EcefToEnu(currentEcef, previous.Reference);
            double overlapThreshold = previous.RangeM + current.RangeM;
            return System.Math.Abs(currentEnu.X) <= overlapThreshold &&
                   System.Math.Abs(currentEnu.Y) <= overlapThreshold;
        }

        private async Task TryRemoveSlotAsync(string slotId, CancellationToken cancellationToken)
        {
            Task removeTask = _resoniteSession.RemoveSlotAsync(slotId, cancellationToken);
            await ObserveCompletionAsync(removeTask).ConfigureAwait(false);

            if (TryGetNonCancellationFailure(removeTask) is { } removeFailure)
            {
                Log.SlotRemovalFailed(_logger, removeFailure, slotId);
            }
        }

        private async Task TryDisconnectResoniteAsync()
        {
            Task disconnectTask = _resoniteSession.DisconnectAsync(CancellationToken.None);
            await ObserveCompletionAsync(disconnectTask).ConfigureAwait(false);

            if (TryGetNonCancellationFailure(disconnectTask) is { } disconnectFailure)
            {
                Log.DisconnectFailed(_logger, disconnectFailure);
            }
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
            Justification = "The caller inspects the completed task to log only non-cancellation failures without rethrowing during cleanup paths.")]
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

        private static TileRunRequest BuildRunRequest(
            InteractiveRunRequest options,
            GeoReference selectionReference,
            GeoReference placementReference,
            double rangeM,
            string runSlotId)
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
                    ManageConnection: false,
                    MeshParentSlotId: runSlotId),
                options.ApiKey);
        }

        private static string BuildRunSlotName(ProbeValues probeValues)
        {
            string lat = probeValues.Latitude.ToString("F5", CultureInfo.InvariantCulture);
            string lon = probeValues.Longitude.ToString("F5", CultureInfo.InvariantCulture);
            string range = probeValues.RangeM.ToString("F0", CultureInfo.InvariantCulture);
            return $"Run {lat}, {lon}, {range}m";
        }

        private static partial class Log
        {
            [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Connecting to Resonite Link at {Host}:{Port}.")]
            public static partial void ConnectingToResonite(ILogger logger, string host, int port);

            [LoggerMessage(
                EventId = 2,
                Level = LogLevel.Information,
                Message = "Probe DV attached: slotId={SlotId} ownsSlot={OwnsSlot} lat={LatPath} lon={LonPath} range={RangePath} search={SearchPath}")]
            public static partial void ProbeBindingAttached(ILogger logger, string slotId, bool ownsSlot, string latPath, string lonPath, string rangePath, string searchPath);

            [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Search query changed: {Query}")]
            public static partial void SearchQueryChanged(ILogger logger, string query);

            [LoggerMessage(
                EventId = 4,
                Level = LogLevel.Information,
                Message = "Probe changed: lat={Lat:F7} lon={Lon:F7} range={Range:F1}m")]
            public static partial void ProbeChanged(ILogger logger, double lat, double lon, double range);

            [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Search resolved: query={Query} name={Name} lat={Latitude:F7} lon={Longitude:F7}")]
            public static partial void SearchResolved(ILogger logger, string query, string name, double latitude, double longitude);

            [LoggerMessage(
                EventId = 6,
                Level = LogLevel.Warning,
                Message = "Failed to resolve search query: {Query}")]
            public static partial void SearchResolutionFailed(ILogger logger, Exception exception, string query);

            [LoggerMessage(
                EventId = 7,
                Level = LogLevel.Information,
                Message = "Run started: slot={SlotId} selectionLat={Lat:F7} selectionLon={Lon:F7} placementLat={PlacementLat:F7} placementLon={PlacementLon:F7} range={Range:F1}m overlap={Overlap}")]
            public static partial void RunStarted(
                ILogger logger,
                string slotId,
                double lat,
                double lon,
                double placementLat,
                double placementLon,
                double range,
                bool overlap);

            [LoggerMessage(
                EventId = 8,
                Level = LogLevel.Information,
                Message = "Run completed: retained={Retained} candidate={Candidate} processed={Processed} streamed={Streamed} failed={Failed}")]
            public static partial void RunCompleted(
                ILogger logger,
                int retained,
                int candidate,
                int processed,
                int streamed,
                int failed);

            [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Run failed.")]
            public static partial void RunFailed(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Run finished with error while superseding.")]
            public static partial void RunSupersededFailed(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Failed to remove slot {SlotId}.")]
            public static partial void SlotRemovalFailed(ILogger logger, Exception exception, string slotId);

            [LoggerMessage(
                EventId = 12,
                Level = LogLevel.Warning,
                Message = "Failed to disconnect Resonite Link cleanly.")]
            public static partial void DisconnectFailed(ILogger logger, Exception exception);

            [LoggerMessage(
                EventId = 13,
                Level = LogLevel.Warning,
                Message = "Search query ignored because GOOGLE_MAPS_API_KEY is not set: query={Query}")]
            public static partial void SearchIgnored(ILogger logger, string query);

            [LoggerMessage(
                EventId = 14,
                Level = LogLevel.Warning,
                Message = "Search query returned no result: query={Query}")]
            public static partial void SearchNoResult(ILogger logger, string query);
        }

        private sealed record RangeFootprint(GeoReference Reference, double RangeM);
    }
}
