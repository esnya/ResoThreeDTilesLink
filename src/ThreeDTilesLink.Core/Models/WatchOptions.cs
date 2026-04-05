namespace ThreeDTilesLink.Core.Models
{
    internal sealed record WatchOptions(
        TimeSpan PollInterval,
        TimeSpan Debounce,
        TimeSpan Throttle,
        WatchConfiguration Configuration);
}
