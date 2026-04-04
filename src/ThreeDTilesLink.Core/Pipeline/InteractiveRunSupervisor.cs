using System.Globalization;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class InteractiveRunSupervisor(
        ITileRunCoordinator tileRunCoordinator,
        IResoniteSession resoniteSession,
        IProbeStore probeStore,
        ISearchResolver searchResolver,
        IClock clock,
        ProbeMonitor probeMonitor,
        ILogger<InteractiveRunSupervisor> logger)
    {
        private readonly ITileRunCoordinator _tileRunCoordinator = tileRunCoordinator;
        private readonly IResoniteSession _resoniteSession = resoniteSession;
        private readonly IProbeStore _probeStore = probeStore;
        private readonly ISearchResolver _searchResolver = searchResolver;
        private readonly IClock _clock = clock;
        private readonly ProbeMonitor _probeMonitor = probeMonitor;
        private readonly ILogger<InteractiveRunSupervisor> _logger = logger;

        public async Task RunAsync(InteractiveRunRequest options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ProbeMonitor.ValidateIntervals(options.ProbeWatch);

            string? activeRunSlotId = null;
            string? completedRunSlotId = null;
            Task<RunSummary>? activeRunTask = null;
            CancellationTokenSource? activeRunCts = null;
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
                    (activeRunTask, activeRunCts, activeRunSlotId, completedRunSlotId) = await ObserveActiveRunAsync(
                        activeRunTask,
                        activeRunCts,
                        activeRunSlotId,
                        completedRunSlotId,
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
                            (activeRunTask, activeRunCts, activeRunSlotId) = await CancelAndCleanupActiveRunAsync(
                                activeRunTask,
                                activeRunCts,
                                activeRunSlotId,
                                cancellationToken).ConfigureAwait(false);

                            completedRunSlotId = await RemoveOldCompletedRunAsync(completedRunSlotId, cancellationToken).ConfigureAwait(false);

                            string runSlotName = BuildRunSlotName(pendingProbeValues);
                            activeRunSlotId = await _resoniteSession.CreateSessionChildSlotAsync(runSlotName, cancellationToken).ConfigureAwait(false);
                            activeRunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            TileRunRequest runRequest = BuildRunRequest(options, pendingProbeValues, activeRunSlotId);

                            activeRunTask = _tileRunCoordinator.RunAsync(runRequest, activeRunCts.Token);
                            lastRunStartedAt = now;
                            _logger.LogInformation(
                                "Run started: slot={SlotId} lat={Lat:F7} lon={Lon:F7} range={Range:F1}m",
                                activeRunSlotId,
                                pendingProbeValues.Latitude,
                                pendingProbeValues.Longitude,
                                pendingProbeValues.RangeM);

                            pendingProbeValues = null;
                            pendingChangedAt = null;
                        }
                    }

                    await _clock.Delay(options.ProbeWatch.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                (activeRunTask, activeRunCts, activeRunSlotId) = await CancelAndCleanupActiveRunAsync(
                    activeRunTask,
                    activeRunCts,
                    activeRunSlotId,
                    CancellationToken.None).ConfigureAwait(false);

                _ = activeRunTask;
                _ = activeRunCts;

                if (!string.IsNullOrWhiteSpace(completedRunSlotId))
                {
                    await TryRemoveSlotAsync(completedRunSlotId, CancellationToken.None).ConfigureAwait(false);
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

        private async Task<(Task<RunSummary>? Task, CancellationTokenSource? Cts, string? ActiveSlotId, string? CompletedSlotId)> ObserveActiveRunAsync(
            Task<RunSummary>? activeRunTask,
            CancellationTokenSource? activeRunCts,
            string? activeRunSlotId,
            string? completedRunSlotId,
            CancellationToken cancellationToken)
        {
            if (activeRunTask is null || !activeRunTask.IsCompleted)
            {
                return (activeRunTask, activeRunCts, activeRunSlotId, completedRunSlotId);
            }

            try
            {
                RunSummary summary = await activeRunTask.ConfigureAwait(false);
                _logger.LogInformation(
                    "Run completed: slot={SlotId} candidate={Candidate} processed={Processed} streamed={Streamed} failed={Failed}",
                    activeRunSlotId ?? "n/a",
                    summary.CandidateTiles,
                    summary.ProcessedTiles,
                    summary.StreamedMeshes,
                    summary.FailedTiles);

                if (!string.IsNullOrWhiteSpace(completedRunSlotId))
                {
                    await TryRemoveSlotAsync(completedRunSlotId, cancellationToken).ConfigureAwait(false);
                }

                completedRunSlotId = activeRunSlotId;
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrWhiteSpace(activeRunSlotId))
                {
                    await TryRemoveSlotAsync(activeRunSlotId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run failed: slot={SlotId}", activeRunSlotId ?? "n/a");
                if (!string.IsNullOrWhiteSpace(activeRunSlotId))
                {
                    await TryRemoveSlotAsync(activeRunSlotId, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                activeRunCts?.Dispose();
            }

            return (null, null, null, completedRunSlotId);
        }

        private async Task<(Task<RunSummary>? Task, CancellationTokenSource? Cts, string? SlotId)> CancelAndCleanupActiveRunAsync(
            Task<RunSummary>? activeRunTask,
            CancellationTokenSource? activeRunCts,
            string? activeRunSlotId,
            CancellationToken cancellationToken)
        {
            if (activeRunTask is null)
            {
                return (null, null, null);
            }

            if (!activeRunTask.IsCompleted)
            {
                activeRunCts?.Cancel();
            }

            try
            {
                _ = await activeRunTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run finished with error while superseding: slot={SlotId}", activeRunSlotId ?? "n/a");
            }
            finally
            {
                activeRunCts?.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(activeRunSlotId))
            {
                await TryRemoveSlotAsync(activeRunSlotId, cancellationToken).ConfigureAwait(false);
            }

            return (null, null, null);
        }

        private async Task<string?> RemoveOldCompletedRunAsync(string? completedRunSlotId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(completedRunSlotId))
            {
                return null;
            }

            await TryRemoveSlotAsync(completedRunSlotId, cancellationToken).ConfigureAwait(false);
            return null;
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

        private static TileRunRequest BuildRunRequest(InteractiveRunRequest options, ProbeValues probeValues, string runSlotId)
        {
            return new TileRunRequest(
                new GeoReference(probeValues.Latitude, probeValues.Longitude, options.HeightOffsetM),
                new TraversalOptions(
                    probeValues.RangeM,
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
    }
}
