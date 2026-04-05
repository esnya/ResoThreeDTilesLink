using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveRunCheckpoint(
        IReadOnlyDictionary<string, Tileset> TilesetCache);
}
