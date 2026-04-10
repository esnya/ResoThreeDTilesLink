using Microsoft.Extensions.Logging;
using ResoniteLink;
using System.Net;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Mesh;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Resonite;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Runtime
{
    internal sealed class TileStreamingRuntime : IAsyncDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ResoniteSession _session;
        private readonly IGeoReferenceResolver _geoReferenceResolver;
        private readonly IDisposable? _geoReferenceResolverDisposable;
        private readonly RunPerformanceSummary? _performanceSummary;
        private bool _disposed;

        internal TileStreamingRuntime(
            ILoggerFactory loggerFactory,
            TimeSpan requestTimeout,
            int maxConcurrentTileProcessing = 8,
            int resoniteSendWorkers = 8,
            bool measurePerformance = false)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            if (requestTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(requestTimeout), "Request timeout must be positive.");
            }

            if (maxConcurrentTileProcessing <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentTileProcessing), "Tile content worker count must be positive.");
            }

            if (resoniteSendWorkers <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(resoniteSendWorkers), "Resonite send worker count must be positive.");
            }

            #pragma warning disable CA2000
            var httpHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                    System.Net.DecompressionMethods.Deflate |
                    System.Net.DecompressionMethods.Brotli,
                EnableMultipleHttp2Connections = true,
                MaxConnectionsPerServer = System.Math.Max(32, maxConcurrentTileProcessing * 4),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
            #pragma warning restore CA2000

            _httpClient = new HttpClient(httpHandler)
            {
                Timeout = requestTimeout,
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };
            _performanceSummary = measurePerformance ? new RunPerformanceSummary() : null;

            var transformer = new GeographicCoordinateTransformer();
            _geoReferenceResolver = new SeaLevelGeoReferenceResolver();
            _geoReferenceResolverDisposable = _geoReferenceResolver as IDisposable;
            var tilesSource = new HttpTilesSource(_httpClient, _performanceSummary);
            var selector = new TileSelector(transformer);
            var traversalCore = new TraversalCore(selector);
            var reconcilerCore = new ResoniteReconcilerCore();
            var extractor = new GlbMeshExtractor();
            var contentProcessor = new TileContentProcessor(tilesSource, extractor, _performanceSummary);
            var meshPlacementService = new MeshPlacementService(transformer);
            var geocodingClient = new GoogleGeocodingClient(_httpClient);
            var searchResolver = new SearchResolver(geocodingClient);
            LinkInterface? linkInterface = null;
            RuntimeResonitePorts resonitePorts;
            try
            {
#pragma warning disable CA2000
                linkInterface = new LinkInterface();
                var resoniteSession = new ResoniteSession(
                    linkInterface,
                    loggerFactory.CreateLogger<ResoniteSession>(),
                    assetImportWorkers: resoniteSendWorkers);
#pragma warning restore CA2000
                resonitePorts = new RuntimeResonitePorts(resoniteSession);
            }
            catch
            {
                linkInterface?.Dispose();
                throw;
            }
            try
            {
                _session = resonitePorts.Session;
                var selectionInputReader = new SelectionInputReader(
                    resonitePorts.InteractiveInputStore,
                    loggerFactory.CreateLogger<SelectionInputReader>());

                SelectionService = new TileSelectionService(
                    tilesSource,
                    traversalCore,
                    reconcilerCore,
                    contentProcessor,
                    meshPlacementService,
                    resonitePorts.SessionControl,
                    resonitePorts.SessionMetadata,
                    loggerFactory.CreateLogger<TileSelectionService>(),
                    maxConcurrentTileProcessing,
                    resoniteSendWorkers,
                    _performanceSummary);

                InteractiveSupervisor = new InteractiveRunSupervisor(
                    SelectionService,
                    resonitePorts.InteractiveSession,
                    resonitePorts.InteractiveInputStore,
                    searchResolver,
                    transformer,
                    _geoReferenceResolver,
                    new SystemClock(),
                    selectionInputReader,
                    loggerFactory.CreateLogger<InteractiveRunSupervisor>());
            }
            catch
            {
                resonitePorts.Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }

        internal TileSelectionService SelectionService { get; }

        internal InteractiveRunSupervisor InteractiveSupervisor { get; }

        internal IGeoReferenceResolver GeoReferenceResolver => _geoReferenceResolver;

        internal Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            return SelectionService.RunAsync(request, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _session.DisposeAsync().ConfigureAwait(false);
            _geoReferenceResolverDisposable?.Dispose();
            _performanceSummary?.Dispose();
            _httpClient.Dispose();
        }
    }
}
