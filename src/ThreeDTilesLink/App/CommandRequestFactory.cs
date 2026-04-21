using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.App
{
    internal static class CommandRequestFactory
    {
        internal static TileRunRequest CreateStreamRequest(
            StreamCommandOptions options,
            TileSourceOptions tileSource,
            IGeoReferenceResolver geoReferenceResolver)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(tileSource);
            ArgumentNullException.ThrowIfNull(geoReferenceResolver);

            GeoReference reference = geoReferenceResolver.Resolve(options.Latitude, options.Longitude, options.HeightOffset);

            return new TileRunRequest(
                reference,
                reference,
                new TraversalOptions(
                    options.RangeM,
                    options.DetailTargetM),
                new SceneOutputOptions(
                    options.ResoniteHost,
                    options.ResonitePort,
                    options.DryRun),
                tileSource);
        }

        internal static InteractiveRunRequest CreateInteractiveRequest(
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
