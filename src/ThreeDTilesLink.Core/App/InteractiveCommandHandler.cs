using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.App
{
    internal static class InteractiveCommandHandler
    {
        internal static InteractiveRunRequest CreateRequest(InteractiveCommandOptions options, string apiKey)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new InteractiveRunRequest(
                options.ResoniteHost,
                options.ResonitePort,
                options.HeightOffset,
                new TraversalOptions(
                    RangeM: 0d,
                    options.DetailTargetM),
                apiKey,
                true,
                new WatchOptions(
                    TimeSpan.FromMilliseconds(options.PollIntervalMs),
                    TimeSpan.FromMilliseconds(options.DebounceMs),
                    TimeSpan.FromMilliseconds(options.ThrottleMs)));
        }
    }
}
