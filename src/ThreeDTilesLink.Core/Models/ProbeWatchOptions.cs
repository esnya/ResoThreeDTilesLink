namespace ThreeDTilesLink.Core.Models
{
    public sealed record ProbeWatchOptions(
        TimeSpan PollInterval,
        TimeSpan Debounce,
        TimeSpan Throttle,
        ProbeConfiguration Probe);
}
