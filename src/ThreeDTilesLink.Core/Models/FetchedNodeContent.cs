using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    public abstract record FetchedNodeContent;

    public sealed record NestedTilesetFetchedContent(Tileset Tileset)
        : FetchedNodeContent;

    public sealed record GlbFetchedContent(byte[] GlbBytes)
        : FetchedNodeContent;

    public sealed record UnsupportedFetchedContent(string? Reason = null)
        : FetchedNodeContent;
}
