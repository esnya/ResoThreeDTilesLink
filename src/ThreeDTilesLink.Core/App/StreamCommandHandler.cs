using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.App
{
    internal static class StreamCommandHandler
    {
        internal static TileRunRequest CreateRequest(
            StreamCommandOptions options,
            string apiKey,
            IGeoReferenceResolver geoReferenceResolver)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(geoReferenceResolver);

            GeoReference reference = geoReferenceResolver.Resolve(options.Latitude, options.Longitude, options.HeightOffset);

            return new TileRunRequest(
                reference,
                reference,
                new TraversalOptions(
                    options.RangeM,
                    options.DetailTargetM),
                new ResoniteOutputOptions(
                    options.ResoniteHost,
                    options.ResonitePort,
                    options.DryRun),
                apiKey);
        }
    }
}
