using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ITilesSource
    {
        Task<Tileset> FetchRootTilesetAsync(GoogleTilesAuth auth, CancellationToken cancellationToken);
        Task<FetchedNodeContent> FetchNodeContentAsync(Uri contentUri, GoogleTilesAuth auth, CancellationToken cancellationToken);
    }
}
