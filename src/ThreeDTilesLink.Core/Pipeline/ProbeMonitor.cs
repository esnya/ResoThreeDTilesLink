using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class ProbeMonitor(
        IProbeStore probeStore,
        ILogger<ProbeMonitor> logger)
    {
        private readonly IProbeStore _probeStore = probeStore;
        private readonly ILogger<ProbeMonitor> _logger = logger;

        public async Task<string?> TryReadProbeSearchAsync(ProbeBinding probeBinding, CancellationToken cancellationToken)
        {
            try
            {
                return NormalizeSearchText(await _probeStore.ReadProbeSearchAsync(probeBinding, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read probe search query.");
                return null;
            }
        }

        public async Task<ProbeValues?> TryReadProbeValuesAsync(ProbeBinding probeBinding, CancellationToken cancellationToken)
        {
            try
            {
                ProbeValues? values = await _probeStore.ReadProbeValuesAsync(probeBinding, cancellationToken).ConfigureAwait(false);
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

        public static bool HasMeaningfulChange(ProbeValues? previous, ProbeValues current)
        {
            ArgumentNullException.ThrowIfNull(current);
            return previous is null ||
                System.Math.Abs(previous.Latitude - current.Latitude) > 1e-5f ||
                System.Math.Abs(previous.Longitude - current.Longitude) > 1e-5f ||
                System.Math.Abs(previous.RangeM - current.RangeM) > 0.1f;
        }

        public static void ValidateIntervals(ProbeWatchOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
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

        private static string? NormalizeSearchText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            return input.Trim();
        }
    }
}
