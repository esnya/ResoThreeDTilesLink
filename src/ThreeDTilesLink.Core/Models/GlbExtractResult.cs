namespace ThreeDTilesLink.Core.Models
{
    internal sealed record GlbExtractResult(
        IReadOnlyList<MeshData> Meshes,
        string? AssetCopyright);
}
