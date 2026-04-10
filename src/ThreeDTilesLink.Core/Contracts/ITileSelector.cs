using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ITileSelector
    {
        IReadOnlyList<TileSelectionResult> Select(
            Tileset tileset,
            GeoReference reference,
            QueryRange range,
            double detailTargetM,
            Matrix4x4d rootParentWorld,
            string idPrefix,
            int depthOffset,
            string? parentContentTileId,
            string? parentContentStableId);
    }
}
