using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface ITileRunCoordinator
    {
        Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken);
        Task<InteractiveTileRunResult> RunInteractiveAsync(
            TileRunRequest request,
            InteractiveRunInput interactive,
            CancellationToken cancellationToken);
    }
}
