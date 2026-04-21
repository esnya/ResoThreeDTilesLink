using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ITilesSource
    {
        Task<Tileset> FetchRootTilesetAsync(TileSourceOptions source, CancellationToken cancellationToken);
        Task<FetchedNodeContent> FetchNodeContentAsync(Uri contentUri, TileSourceOptions source, CancellationToken cancellationToken);
    }
}
