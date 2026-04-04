namespace ThreeDTilesLink.Core.Models;

public sealed record GlbExtractResult(
    IReadOnlyList<MeshData> Meshes,
    string? AssetCopyright);
