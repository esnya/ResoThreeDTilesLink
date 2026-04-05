using ThreeDTilesLink.Core.Math;

namespace ThreeDTilesLink.Core.Models
{
    internal sealed record TileSelectionResult(
        string TileId,
        Uri ContentUri,
        Matrix4x4d WorldTransform,
        int Depth,
        string? ParentTileId,
        TileContentKind ContentKind,
        bool HasChildren,
        double? HorizontalSpanM,
        string? StableId = null,
        string? ParentStableId = null);
}
