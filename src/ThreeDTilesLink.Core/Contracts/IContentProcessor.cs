using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IContentProcessor
    {
        Task<ContentProcessResult> ProcessAsync(
            TileSelectionResult tile,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken);
    }
}
