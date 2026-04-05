using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    internal abstract record FetchedNodeContent;

    internal sealed record NestedTilesetFetchedContent(Tileset Tileset)
        : FetchedNodeContent;

    internal sealed record GlbFetchedContent(byte[] GlbBytes)
        : FetchedNodeContent;

    internal sealed record UnsupportedFetchedContent(string? Reason = null)
        : FetchedNodeContent;
}
