using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    public abstract record ContentProcessResult;

    public sealed record NestedTilesetContentProcessResult(Tileset Tileset)
        : ContentProcessResult;

    public sealed record RenderableContentProcessResult(
        IReadOnlyList<MeshData> Meshes,
        string? AssetCopyright)
        : ContentProcessResult;

    public sealed record SkippedContentProcessResult(string? Reason = null)
        : ContentProcessResult;
}
