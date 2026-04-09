using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TileRunCoordinator : ITileRunCoordinator
    {
        private readonly TileSelectionService _selectionService;

        internal TileRunCoordinator(
            ITilesSource tilesSource,
            TraversalCore traversalCore,
            ResoniteReconcilerCore reconcilerCore,
            IContentProcessor contentProcessor,
            IMeshPlacementService meshPlacementService,
            IResoniteSession resoniteSession,
            IResoniteSessionMetadataPort sessionMetadataPort,
            ILogger<TileRunCoordinator> logger,
            int maxConcurrentTileProcessing = 1)
        {
            ArgumentNullException.ThrowIfNull(logger);

            _selectionService = new TileSelectionService(
                tilesSource,
                traversalCore,
                reconcilerCore,
                contentProcessor,
                meshPlacementService,
                resoniteSession,
                sessionMetadataPort,
                logger,
                maxConcurrentTileProcessing);
        }

        public Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            return _selectionService.RunAsync(request, cancellationToken);
        }

        public Task<InteractiveTileRunResult> RunInteractiveAsync(
            TileRunRequest request,
            InteractiveRunInput interactive,
            CancellationToken cancellationToken)
        {
            return _selectionService.RunInteractiveAsync(request, interactive, cancellationToken);
        }
    }
}
