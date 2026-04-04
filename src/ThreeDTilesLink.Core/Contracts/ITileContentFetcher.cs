using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface ITileContentFetcher
    {
        Task<Tileset> FetchRootTilesetAsync(GoogleTilesAuth auth, CancellationToken cancellationToken);
        Task<Tileset> FetchTilesetAsync(Uri tilesetUri, GoogleTilesAuth auth, CancellationToken cancellationToken);
        Task<byte[]> FetchTileContentAsync(Uri contentUri, GoogleTilesAuth auth, CancellationToken cancellationToken);
    }
}
