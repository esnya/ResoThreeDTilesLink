using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    public sealed record InteractiveRunCheckpoint(
        IReadOnlyDictionary<string, Tileset> TilesetCache);
}
