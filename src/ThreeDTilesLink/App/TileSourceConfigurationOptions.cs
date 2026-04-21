using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.App
{
    internal sealed class TileSourceConfigurationOptions
    {
        public string RootTilesetUri { get; set; } = TileSourceDefaults.GoogleRootTilesetUri.AbsoluteUri;

        public string? ApiKey { get; set; }

        public string? BearerToken { get; set; }

        public string? FileSchemeBaseUri { get; set; } = TileSourceDefaults.GoogleFileSchemeBaseUri.AbsoluteUri;

        public string[] InheritedQueryParameters { get; set; } = ["session"];

        internal TileSourceOptions ToModel()
        {
            if (!Uri.TryCreate(RootTilesetUri, UriKind.Absolute, out Uri? rootTilesetUri))
            {
                throw new InvalidOperationException($"Tile source root URI must be absolute: {RootTilesetUri}");
            }

            Uri? fileSchemeBaseUri = null;
            if (!string.IsNullOrWhiteSpace(FileSchemeBaseUri))
            {
                if (!Uri.TryCreate(FileSchemeBaseUri, UriKind.Absolute, out fileSchemeBaseUri))
                {
                    throw new InvalidOperationException($"Tile source file-scheme base URI must be absolute: {FileSchemeBaseUri}");
                }
            }

            string[] inheritedQueryParameters = (InheritedQueryParameters ?? Array.Empty<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return new TileSourceOptions(
                rootTilesetUri,
                new TileSourceAccess(ApiKey, BearerToken),
                new TileSourceContentLinkOptions(fileSchemeBaseUri, inheritedQueryParameters));
        }
    }
}
