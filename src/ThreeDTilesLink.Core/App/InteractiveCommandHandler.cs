using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.App
{
    internal static class InteractiveCommandHandler
    {
        internal static InteractiveRunRequest CreateRequest(
            InteractiveCommandOptions options,
            TileSourceOptions tileSource,
            SearchOptions search)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(tileSource);
            ArgumentNullException.ThrowIfNull(search);

            return new InteractiveRunRequest(
                options.ResoniteHost,
                options.ResonitePort,
                options.HeightOffset,
                new TraversalOptions(
                    RangeM: 0d,
                    options.DetailTargetM),
                tileSource,
                search,
                true,
                new WatchOptions(
                    TimeSpan.FromMilliseconds(options.PollIntervalMs),
                    TimeSpan.FromMilliseconds(options.DebounceMs),
                    TimeSpan.FromMilliseconds(options.ThrottleMs)));
        }
    }
}
