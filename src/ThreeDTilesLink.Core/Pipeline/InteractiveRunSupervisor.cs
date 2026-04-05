using System.Globalization;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class InteractiveRunSupervisor(
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
                _logger.LogInformation("Connecting to Resonite Link at {Host}:{Port}", options.ResoniteHost, options.ResonitePort);
                await _resoniteSession.ConnectAsync(options.ResoniteHost, options.ResonitePort, cancellationToken).ConfigureAwait(false);
                connected = true;

                probeBinding = await _probeStore.CreateProbeAsync(options.ProbeWatch.Probe, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Probe DV attached: slotId={SlotId} ownsSlot={OwnsSlot} lat={LatPath} lon={LonPath} range={RangePath} search={SearchPath}",
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
                            _logger.LogInformation("Search query changed: {Query}", currentSearch);
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
                        _logger.LogInformation(
                            "Probe changed: lat={Lat:F7} lon={Lon:F7} range={Range:F1}m",
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
                            _logger.LogInformation(
                                "Run started: slot={SlotId} selectionLat={Lat:F7} selectionLon={Lon:F7} placementLat={PlacementLat:F7} placementLon={PlacementLon:F7} range={Range:F1}m overlap={Overlap}",
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

                if (!string.IsNullOrWhiteSpace(sessionSlotId))
                {
                    await TryRemoveSlotAsync(sessionSlotId, CancellationToken.None).ConfigureAwait(false);
                }

                if (probeBinding is not null && probeBinding.OwnsSlot && !string.IsNullOrWhiteSpace(probeBinding.SlotId))
                {
                    await TryRemoveSlotAsync(probeBinding.SlotId, CancellationToken.None).ConfigureAwait(false);
                }

                if (connected)
                {
                    try
                    {
                        await _resoniteSession.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to disconnect Resonite Link cleanly.");
                    }
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
                _logger.LogWarning("Search query ignored because GOOGLE_MAPS_API_KEY is not set: query={Query}", searchText);
                return null;
            }

            try
            {
                LocationSearchResult? result = await _searchResolver.SearchAsync(options.ApiKey, searchText, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    _logger.LogWarning("Search query returned no result: query={Query}", searchText);
                    return null;
                }

                await _probeStore.UpdateProbeCoordinatesAsync(probeBinding, result.Latitude, result.Longitude, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Search resolved: query={Query} name={Name} lat={Lat:F7} lon={Lon:F7}",
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve search query: {Query}", searchText);
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

            try
            {
                InteractiveTileRunResult result = await activeRunTask.ConfigureAwait(false);
                retainedTiles = new Dictionary<string, RetainedTileState>(result.VisibleTiles, StringComparer.Ordinal);
                checkpoint = result.Checkpoint;
                _logger.LogInformation(
                    "Run completed: retained={Retained} candidate={Candidate} processed={Processed} streamed={Streamed} failed={Failed}",
                    retainedTiles.Count,
                    result.Summary.CandidateTiles,
                    result.Summary.ProcessedTiles,
                    result.Summary.StreamedMeshes,
                    result.Summary.FailedTiles);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run failed.");
            }
            finally
            {
                activeRunCts?.Dispose();
            }

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
                activeRunCts?.Cancel();
            }

            try
            {
                InteractiveTileRunResult result = await activeRunTask.ConfigureAwait(false);
                retainedTiles = new Dictionary<string, RetainedTileState>(result.VisibleTiles, StringComparer.Ordinal);
                checkpoint = result.Checkpoint;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run finished with error while superseding.");
            }
            finally
            {
                activeRunCts?.Dispose();
            }

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
            try
            {
                await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove slot {SlotId}.", slotId);
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

        private sealed record RangeFootprint(GeoReference Reference, double RangeM);
    }
}
