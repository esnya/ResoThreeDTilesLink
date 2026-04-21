namespace ThreeDTilesLink.Core.Models
{
    internal sealed record TileSourceContentLinkOptions(
        Uri? FileSchemeBaseUri,
        IReadOnlyList<string> InheritedQueryParameters)
    {
        internal static TileSourceContentLinkOptions CreateDefault()
        {
            return new TileSourceContentLinkOptions(
                FileSchemeBaseUri: null,
                InheritedQueryParameters: Array.Empty<string>());
        }

        internal static TileSourceContentLinkOptions CreateGoogleDefaults()
        {
            return new TileSourceContentLinkOptions(
                TileSourceDefaults.GoogleFileSchemeBaseUri,
                ["session"]);
        }
    }
}
