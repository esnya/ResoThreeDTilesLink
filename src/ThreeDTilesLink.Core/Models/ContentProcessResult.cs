using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    internal abstract record ContentProcessResult;

    internal sealed record NestedTilesetContentProcessResult(Tileset Tileset)
        : ContentProcessResult;

    internal sealed record RenderableContentProcessResult(
        IReadOnlyList<MeshData> Meshes,
        string? AssetCopyright)
        : ContentProcessResult;

    internal sealed record SkippedContentProcessResult(string? Reason = null)
        : ContentProcessResult;
}
