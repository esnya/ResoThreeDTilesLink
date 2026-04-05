using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Runtime;

namespace ThreeDTilesLink.Core.App
{
    internal static class StreamCommandHandler
    {
        internal static TileRunRequest CreateRequest(StreamCommandOptions options, string apiKey)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new TileRunRequest(
                new GeoReference(options.Latitude, options.Longitude, options.HeightOffsetM),
                new GeoReference(options.Latitude, options.Longitude, options.HeightOffsetM),
                new TraversalOptions(
                    options.RangeM,
                    options.TileLimit,
                    options.DepthLimit,
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

            RunSummary summary = await runtime.RunAsync(CreateRequest(options, apiKey), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"CandidateTiles={summary.CandidateTiles} ProcessedTiles={summary.ProcessedTiles} StreamedMeshes={summary.StreamedMeshes} FailedTiles={summary.FailedTiles}")
                .ConfigureAwait(false);
            return 0;
        }
    }
}
