using Microsoft.Extensions.Logging;
using ResoniteLink;
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
        private bool _disposed;

        internal TileStreamingRuntime(
            ILoggerFactory loggerFactory,
            TimeSpan requestTimeout,
            int maxConcurrentTileProcessing = 8,
            int resoniteSendWorkers = 1)
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

            _httpClient = new HttpClient
            {
                Timeout = requestTimeout
            };

            var transformer = new GeographicCoordinateTransformer();
            var tilesSource = new HttpTilesSource(_httpClient);
            var selector = new TileSelector(transformer);
            var traversalCore = new TraversalCore(selector);
            var extractor = new GlbMeshExtractor();
            var contentProcessor = new TileContentProcessor(tilesSource, extractor);
            var meshPlacementService = new MeshPlacementService(transformer);
            var geocodingClient = new GoogleGeocodingClient(_httpClient);
            var searchResolver = new SearchResolver(geocodingClient);
            LinkInterface? linkInterface = null;
            ResoniteSession resoniteSession;
            try
            {
#pragma warning disable CA2000
                linkInterface = new LinkInterface();
#pragma warning restore CA2000
                resoniteSession = new ResoniteSession(
                    linkInterface,
                    loggerFactory.CreateLogger<ResoniteSession>(),
                    assetImportWorkers: resoniteSendWorkers);
            }
            catch
            {
                linkInterface?.Dispose();
                throw;
            }
            _session = resoniteSession;

            var selectedTileProjector = new ResoniteSelectedTileProjector(resoniteSession);
            var selectionInputReader = new SelectionInputReader(
                resoniteSession,
                loggerFactory.CreateLogger<SelectionInputReader>());

            SelectionService = new TileSelectionService(
                tilesSource,
                traversalCore,
                contentProcessor,
                meshPlacementService,
                selectedTileProjector,
                loggerFactory.CreateLogger<TileSelectionService>(),
                maxConcurrentTileProcessing,
                resoniteSendWorkers);

            InteractiveSupervisor = new InteractiveRunSupervisor(
                SelectionService,
                resoniteSession,
                resoniteSession,
                searchResolver,
                transformer,
                new SystemClock(),
                selectionInputReader,
                loggerFactory.CreateLogger<InteractiveRunSupervisor>());
        }

        internal TileSelectionService SelectionService { get; }

        internal InteractiveRunSupervisor InteractiveSupervisor { get; }

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
            _httpClient.Dispose();
        }
    }
}
