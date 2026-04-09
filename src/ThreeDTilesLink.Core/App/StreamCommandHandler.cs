using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Runtime;

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

        internal static async Task<int> RunAsync(
            StreamCommandOptions options,
            TileStreamingRuntime runtime,
            string apiKey,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(output);

            RunSummary summary = await runtime.RunAsync(
                CreateRequest(options, apiKey, runtime.GeoReferenceResolver),
                cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"CandidateTiles={summary.CandidateTiles} ProcessedTiles={summary.ProcessedTiles} StreamedMeshes={summary.StreamedMeshes} FailedTiles={summary.FailedTiles}")
                .ConfigureAwait(false);
            return 0;
        }
    }
}
