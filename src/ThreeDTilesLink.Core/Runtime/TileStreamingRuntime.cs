using Microsoft.Extensions.Logging;
using ResoniteLink;
using ThreeDTilesLink.Core.Auth;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Mesh;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Runtime
{
    public sealed class TileStreamingRuntime : IAsyncDisposable
    {
        private readonly HttpClient _httpClient;
        private bool _disposed;

        public TileStreamingRuntime(ILoggerFactory loggerFactory, TimeSpan requestTimeout)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            if (requestTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(requestTimeout), "Request timeout must be positive.");
            }

            _httpClient = new HttpClient
            {
                Timeout = requestTimeout
            };

            var transformer = new GeographicCoordinateTransformer();
            var fetcher = new HttpTileContentFetcher(_httpClient);
            var selector = new TileSelector(transformer);
            var scheduler = new DefaultTileStreamingScheduler(
                selector,
                loggerFactory.CreateLogger<DefaultTileStreamingScheduler>());
            var extractor = new GlbMeshExtractor();
            var tokenProvider = new AdcAccessTokenProvider();
            GeocodingClient = new GoogleGeocodingClient(_httpClient);

            StreamingService = new TileStreamingService(
                fetcher,
                scheduler,
                extractor,
                transformer,
                new LinkInterface(),
                tokenProvider,
                loggerFactory.CreateLogger<TileStreamingService>());
        }

        public TileStreamingService StreamingService { get; }

        public GoogleGeocodingClient GeocodingClient { get; }

        public Task<RunSummary> RunAsync(StreamerOptions options, CancellationToken cancellationToken)
        {
            return StreamingService.RunAsync(options, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StreamingService.DisposeAsync().ConfigureAwait(false);
            _httpClient.Dispose();
        }
    }
}
