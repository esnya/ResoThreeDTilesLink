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
        private readonly IDisposable _geoReferenceResolverDisposable;
        private readonly RunPerformanceSummary? _performanceSummary;
        private bool _disposed;

        private TileStreamingRuntime(
            HttpClient httpClient,
            ResoniteSession session,
            IGeoReferenceResolver geoReferenceResolver,
            IDisposable geoReferenceResolverDisposable,
            RunPerformanceSummary? performanceSummary,
            TileSelectionService selectionService,
            InteractiveRunSupervisor interactiveSupervisor)
        {
            _httpClient = httpClient;
            _session = session;
            _geoReferenceResolver = geoReferenceResolver;
            _geoReferenceResolverDisposable = geoReferenceResolverDisposable;
            _performanceSummary = performanceSummary;
            SelectionService = selectionService;
            InteractiveSupervisor = interactiveSupervisor;
        }

        internal static async Task<TileStreamingRuntime> CreateAsync(
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

            var httpClient = new HttpClient(httpHandler)
            {
                Timeout = requestTimeout,
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };

            RunPerformanceSummary? performanceSummary = measurePerformance ? new RunPerformanceSummary() : null;
            IGeoReferenceResolver geoReferenceResolver = new SeaLevelGeoReferenceResolver();
            var geoReferenceResolverDisposable = (IDisposable)geoReferenceResolver;
            LinkInterface? linkInterface = null;
            ResoniteSession? session = null;
            try
            {
                var transformer = new GeographicCoordinateTransformer();
                var tilesSource = new HttpTilesSource(httpClient, performanceSummary);
                var selector = new TileSelector(transformer);
                var traversalCore = new TraversalCore(selector);
                var reconcilerCore = new ResoniteReconcilerCore();
                var extractor = new GlbMeshExtractor();
                var contentProcessor = new TileContentProcessor(tilesSource, extractor, performanceSummary);
                var meshPlacementService = new MeshPlacementService(transformer);
                var geocodingClient = new GoogleGeocodingClient(httpClient);
                var searchResolver = new SearchResolver(geocodingClient);

#pragma warning disable CA2000
                linkInterface = new LinkInterface();
                session = new ResoniteSession(
                    linkInterface,
                    loggerFactory.CreateLogger<ResoniteSession>(),
                    assetImportWorkers: resoniteSendWorkers);
#pragma warning restore CA2000

                var resonitePorts = new RuntimeResonitePorts(session);
                var selectionInputReader = new SelectionInputReader(
                    resonitePorts.InteractiveInputStore,
                    loggerFactory.CreateLogger<SelectionInputReader>());

                var selectionService = new TileSelectionService(
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
                    performanceSummary);

                var interactiveSupervisor = new InteractiveRunSupervisor(
                    selectionService,
                    resonitePorts.InteractiveSession,
                    resonitePorts.InteractiveInputStore,
                    searchResolver,
                    transformer,
                    geoReferenceResolver,
                    new SystemClock(),
                    selectionInputReader,
                    loggerFactory,
                    loggerFactory.CreateLogger<InteractiveRunSupervisor>());

                return new TileStreamingRuntime(
                    httpClient,
                    session,
                    geoReferenceResolver,
                    geoReferenceResolverDisposable,
                    performanceSummary,
                    selectionService,
                    interactiveSupervisor);
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    linkInterface?.Dispose();
                }

                geoReferenceResolverDisposable.Dispose();
                performanceSummary?.Dispose();
                httpClient.Dispose();

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
            _geoReferenceResolverDisposable.Dispose();
            _performanceSummary?.Dispose();
            _httpClient.Dispose();
        }
    }
}
