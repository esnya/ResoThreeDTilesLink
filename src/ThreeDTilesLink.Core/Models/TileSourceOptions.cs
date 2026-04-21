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

        internal static TileSourceOptions CreateGoogleDefaults(string? apiKey)
        {
            return new TileSourceOptions(
                TileSourceDefaults.GoogleRootTilesetUri,
                new TileSourceAccess(apiKey, null),
                TileSourceContentLinkOptions.CreateGoogleDefaults());
        }
    }
}
