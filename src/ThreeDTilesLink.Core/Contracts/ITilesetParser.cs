using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ITilesetParser
    {
        Tileset Parse(string json, TileSourceContentLinkOptions contentLinks, Uri sourceUri);
    }
}
