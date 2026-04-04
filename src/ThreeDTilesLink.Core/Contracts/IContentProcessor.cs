using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface IContentProcessor
    {
        Task<ContentProcessResult> ProcessAsync(
            TileSelectionResult tile,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken);
    }
}
