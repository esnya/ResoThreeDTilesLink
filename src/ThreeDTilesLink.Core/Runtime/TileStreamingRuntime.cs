using Microsoft.Extensions.Logging;
using ResoniteLink;
using ThreeDTilesLink.Core.Auth;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Mesh;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Resonite;
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
            var tilesSource = new HttpTilesSource(_httpClient);
            var selector = new TileSelector(transformer);
            var traversalPlanner = new TraversalPlanner(
                selector,
                loggerFactory.CreateLogger<TraversalPlanner>());
            var extractor = new GlbMeshExtractor();
            var contentProcessor = new TileContentProcessor(tilesSource, extractor);
            var meshPlacementService = new MeshPlacementService(transformer);
            var tokenProvider = new AdcAccessTokenProvider();
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
                    loggerFactory.CreateLogger<ResoniteSession>());
            }
            catch
            {
                linkInterface?.Dispose();
                throw;
            }

            var probeMonitor = new ProbeMonitor(
                resoniteSession,
                loggerFactory.CreateLogger<ProbeMonitor>());

            RunCoordinator = new TileRunCoordinator(
                tilesSource,
                traversalPlanner,
                contentProcessor,
                meshPlacementService,
                resoniteSession,
                tokenProvider,
                loggerFactory.CreateLogger<TileRunCoordinator>());

            InteractiveSupervisor = new InteractiveRunSupervisor(
                RunCoordinator,
                resoniteSession,
                resoniteSession,
                searchResolver,
                new SystemClock(),
                probeMonitor,
                loggerFactory.CreateLogger<InteractiveRunSupervisor>());

            Session = resoniteSession;
        }

        public TileRunCoordinator RunCoordinator { get; }

        public InteractiveRunSupervisor InteractiveSupervisor { get; }

        public ResoniteSession Session { get; }

        public Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            return RunCoordinator.RunAsync(request, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Session.DisposeAsync().ConfigureAwait(false);
            _httpClient.Dispose();
        }
    }
}
