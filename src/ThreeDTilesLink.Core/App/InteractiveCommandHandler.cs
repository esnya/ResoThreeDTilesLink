using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.App
{
    internal static class InteractiveCommandHandler
    {
        internal static InteractiveRunRequest CreateRequest(InteractiveCommandOptions options, string apiKey)
        {
            ArgumentNullException.ThrowIfNull(options);

            InteractiveWatchPath watchPath = InteractiveWatchPath.Parse(options.WatchPath);

            return new InteractiveRunRequest(
                options.ResoniteHost,
                options.ResonitePort,
                options.HeightOffset,
                new TraversalOptions(
                    RangeM: 0d,
                    options.TileLimit,
                    options.DepthLimit,
                    options.DetailTargetM),
                options.DryRun,
                apiKey,
                options.RemoveOutOfRange,
                new WatchOptions(
                    TimeSpan.FromMilliseconds(options.PollIntervalMs),
                    TimeSpan.FromMilliseconds(options.DebounceMs),
                    TimeSpan.FromMilliseconds(options.ThrottleMs),
                    new WatchConfiguration(
                        watchPath.LatitudePath,
                        watchPath.LongitudePath,
                        watchPath.RangePath,
                        watchPath.SearchPath)));
        }
    }
}
