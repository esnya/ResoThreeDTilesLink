namespace ThreeDTilesLink.Core.Models
{
    internal sealed record TileSourceOptions(
        Uri RootTilesetUri,
        TileSourceAccess Access,
        TileSourceContentLinkOptions ContentLinks)
    {
        internal TileSourceOptions(
            Uri rootTilesetUri,
            TileSourceAccess access)
            : this(rootTilesetUri, access, TileSourceContentLinkOptions.CreateDefault())
        {
        }
    }
}
