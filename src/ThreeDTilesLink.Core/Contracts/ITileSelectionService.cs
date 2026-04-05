using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ITileSelectionService
    {
        Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken);
        Task<InteractiveTileRunResult> RunInteractiveAsync(
            TileRunRequest request,
            InteractiveRunInput interactive,
            CancellationToken cancellationToken);
    }
}
