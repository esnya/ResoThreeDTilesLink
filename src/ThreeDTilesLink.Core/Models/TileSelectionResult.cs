using ThreeDTilesLink.Core.Math;

namespace ThreeDTilesLink.Core.Models;

public sealed record TileSelectionResult(
    string TileId,
    Uri ContentUri,
    Matrix4x4d WorldTransform,
    int Depth,
    bool HasChildren,
    double? HorizontalSpanM);
