using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface ITileSelector
    {
        IReadOnlyList<TileSelectionResult> Select(
            Tileset tileset,
            GeoReference reference,
            QuerySquare square,
            int maxDepth,
            double detailTargetM,
            int maxTiles,
            Matrix4x4d rootParentWorld,
            string idPrefix,
            int depthOffset,
            string? parentContentTileId,
            string? parentContentStableId);
    }
}
