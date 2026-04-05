namespace ThreeDTilesLink.Core.Models
{
    internal sealed record ProbeWatchOptions(
        TimeSpan PollInterval,
        TimeSpan Debounce,
        TimeSpan Throttle,
        ProbeConfiguration Probe);
}
