using Microsoft.Extensions.Logging;
using System.Globalization;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class ProbeDrivenStreamingService(
        TileStreamingService tileStreamingService,
        GoogleGeocodingClient geocodingClient,
        ILogger<ProbeDrivenStreamingService> logger)
    {
        private readonly TileStreamingService _tileStreamingService = tileStreamingService;
        private readonly GoogleGeocodingClient _geocodingClient = geocodingClient;
        private readonly ILogger<ProbeDrivenStreamingService> _logger = logger;

        public async Task RunAsync(ProbeDrivenStreamerOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ValidateIntervals(options);

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
            DateTimeOffset lastRunStartedAt = DateTimeOffset.MinValue;
            bool connected = false;

            try
            {
                _logger.LogInformation("Connecting to Resonite Link at {Host}:{Port}", options.ResoniteHost, options.ResonitePort);
                await _tileStreamingService.ConnectAsync(options.ResoniteHost, options.ResonitePort, cancellationToken).ConfigureAwait(false);
                connected = true;

                probeBinding = await _tileStreamingService.CreateProbeAsync(options.Probe, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Probe DV attached: slotId={SlotId} ownsSlot={OwnsSlot} lat={LatPath} lon={LonPath} range={RangePath} search={SearchPath}",
                    probeBinding.SlotId,
                    probeBinding.OwnsSlot,
                    options.Probe.LatitudeVariablePath,
                    options.Probe.LongitudeVariablePath,
                    options.Probe.RangeVariablePath,
                    options.Probe.SearchVariablePath);

                while (!cancellationToken.IsCancellationRequested)
                {
                    (activeRunTask, activeRunCts, activeRunSlotId, completedRunSlotId) = await ObserveActiveRunAsync(
                        activeRunTask,
                        activeRunCts,
                        activeRunSlotId,
                        completedRunSlotId,
                        cancellationToken).ConfigureAwait(false);

                    string? currentSearch = await TryReadProbeSearchAsync(probeBinding, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(lastObservedSearch, currentSearch, StringComparison.Ordinal))
                    {
                        lastObservedSearch = currentSearch;
                        if (currentSearch is null)
                        {
                            pendingSearch = null;
                            pendingSearchChangedAt = null;
                            lastResolvedSearch = null;
                        }
                        else
                        {
                            pendingSearch = currentSearch;
                            pendingSearchChangedAt = DateTimeOffset.UtcNow;
                            _logger.LogInformation("Search query changed: {Query}", currentSearch);
                        }
                    }

                    if (pendingSearch is not null &&
                        pendingSearchChangedAt is not null &&
                        DateTimeOffset.UtcNow - pendingSearchChangedAt.Value >= options.Debounce &&
                        !string.Equals(lastResolvedSearch, pendingSearch, StringComparison.Ordinal))
                    {
                        await ResolveSearchAsync(options, probeBinding, pendingSearch, cancellationToken).ConfigureAwait(false);
                        lastResolvedSearch = pendingSearch;
                        pendingSearch = null;
                        pendingSearchChangedAt = null;
                    }

                    ProbeValues? currentProbe = await TryReadProbeValuesAsync(probeBinding, cancellationToken).ConfigureAwait(false);
                    if (currentProbe is not null && HasMeaningfulChange(lastProbeValues, currentProbe))
                    {
                        pendingProbeValues = currentProbe;
                        pendingChangedAt = DateTimeOffset.UtcNow;
                        lastProbeValues = currentProbe;
                        _logger.LogInformation(
                            "Probe changed: lat={Lat:F7} lon={Lon:F7} range={Range:F1}m",
                            currentProbe.Latitude,
                            currentProbe.Longitude,
                            currentProbe.RangeM);
                    }

                    if (pendingProbeValues is not null && pendingChangedAt is not null)
                    {
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        bool debounceElapsed = now - pendingChangedAt.Value >= options.Debounce;
                        bool throttleElapsed = lastRunStartedAt == DateTimeOffset.MinValue || now - lastRunStartedAt >= options.Throttle;

                        if (debounceElapsed && throttleElapsed)
                        {
                            (activeRunTask, activeRunCts, activeRunSlotId) = await CancelAndCleanupActiveRunAsync(
                                activeRunTask,
                                activeRunCts,
                                activeRunSlotId,
                                cancellationToken).ConfigureAwait(false);

                            completedRunSlotId = await RemoveOldCompletedRunAsync(completedRunSlotId, cancellationToken).ConfigureAwait(false);

                            string runSlotName = BuildRunSlotName(pendingProbeValues);
                            activeRunSlotId = await _tileStreamingService.CreateSessionChildSlotAsync(runSlotName, cancellationToken).ConfigureAwait(false);
                            activeRunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            StreamerOptions runOptions = BuildRunOptions(options, pendingProbeValues, activeRunSlotId);

                            activeRunTask = _tileStreamingService.RunAsync(runOptions, activeRunCts.Token);
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

                    await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
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
                        await _tileStreamingService.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to disconnect Resonite Link cleanly.");
                    }
                }
            }
        }

        private async Task ResolveSearchAsync(
            ProbeDrivenStreamerOptions options,
            ProbeBinding probeBinding,
            string searchText,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                _logger.LogWarning("Search query ignored because GOOGLE_MAPS_API_KEY is not set: query={Query}", searchText);
                return;
            }

            try
            {
                LocationSearchResult? result = await _geocodingClient.SearchAsync(options.ApiKey, searchText, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    _logger.LogWarning("Search query returned no result: query={Query}", searchText);
                    return;
                }

                await _tileStreamingService.UpdateProbeCoordinatesAsync(
                    probeBinding,
                    result.Latitude,
                    result.Longitude,
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Search resolved: query={Query} name={Name} lat={Lat:F7} lon={Lon:F7}",
                    searchText,
                    result.FormattedAddress,
                    result.Latitude,
                    result.Longitude);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve search query: {Query}", searchText);
            }
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
                await _tileStreamingService.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove slot {SlotId}.", slotId);
            }
        }

        private async Task<string?> TryReadProbeSearchAsync(ProbeBinding probeBinding, CancellationToken cancellationToken)
        {
            try
            {
                return NormalizeSearchText(await _tileStreamingService.ReadProbeSearchAsync(probeBinding, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read probe search query.");
                return null;
            }
        }

        private async Task<ProbeValues?> TryReadProbeValuesAsync(ProbeBinding probeBinding, CancellationToken cancellationToken)
        {
            try
            {
                ProbeValues? values = await _tileStreamingService.ReadProbeValuesAsync(probeBinding, cancellationToken).ConfigureAwait(false);
                if (values is null || values.RangeM <= 0d)
                {
                    return null;
                }

                return values;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read probe values.");
                return null;
            }
        }

        private static StreamerOptions BuildRunOptions(ProbeDrivenStreamerOptions options, ProbeValues probeValues, string runSlotId)
        {
            return new StreamerOptions(
                new GeoReference(probeValues.Latitude, probeValues.Longitude, options.HeightOffsetM),
                probeValues.RangeM,
                options.ResoniteHost,
                options.ResonitePort,
                options.MaxTiles,
                options.MaxDepth,
                options.DetailTargetM,
                options.DryRun,
                options.ApiKey,
                options.BootstrapRangeMultiplier,
                ManageResoniteConnection: false,
                MeshParentSlotId: runSlotId);
        }

        private static string BuildRunSlotName(ProbeValues probeValues)
        {
            string lat = probeValues.Latitude.ToString("F5", CultureInfo.InvariantCulture);
            string lon = probeValues.Longitude.ToString("F5", CultureInfo.InvariantCulture);
            string range = probeValues.RangeM.ToString("F0", CultureInfo.InvariantCulture);
            return $"Run {lat}, {lon}, {range}m";
        }

        private static bool HasMeaningfulChange(ProbeValues? previous, ProbeValues current)
        {
            return previous is null ||
                System.Math.Abs(previous.Latitude - current.Latitude) > 1e-5f ||
                System.Math.Abs(previous.Longitude - current.Longitude) > 1e-5f ||
                System.Math.Abs(previous.RangeM - current.RangeM) > 0.1f;
        }

        private static string? NormalizeSearchText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            return input.Trim();
        }

        private static void ValidateIntervals(ProbeDrivenStreamerOptions options)
        {
            if (options.PollInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Poll interval must be positive.");
            }

            if (options.Debounce < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Debounce must be zero or positive.");
            }

            if (options.Throttle < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Throttle must be zero or positive.");
            }
        }
    }
}
