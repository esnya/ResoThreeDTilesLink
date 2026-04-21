using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using System.Web;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles
{
    internal sealed class TilesetParser : ITilesetParser
    {
        public Tileset Parse(string json, TileSourceContentLinkOptions contentLinks, Uri sourceUri)
        {
            ArgumentNullException.ThrowIfNull(contentLinks);

            var context = new ParseContext();
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            return !root.TryGetProperty("root", out JsonElement rootTile)
                ? throw new InvalidOperationException("tileset root is missing.")
                : new Tileset(ParseTile(rootTile, contentLinks, sourceUri, "0", "0", context));
        }

        private static Tile ParseTile(
            JsonElement tileElement,
            TileSourceContentLinkOptions contentLinks,
            Uri sourceUri,
            string displayLabel,
            string stablePath,
            ParseContext context)
        {
            var children = new List<Tile>();
            if (tileElement.TryGetProperty("children", out JsonElement childrenElement) && childrenElement.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement child in childrenElement.EnumerateArray())
                {
                    children.Add(ParseTile(
                        child,
                        contentLinks,
                        sourceUri,
                        $"{displayLabel}{EncodeDisplayIdSegment(index, context)}",
                        $"{stablePath}/{index}",
                        context));
                    index++;
                }
            }

            IReadOnlyList<double>? transform = ParseDoubleArray(tileElement, "transform");
            BoundingVolume? bounding = ParseBoundingVolume(tileElement);
            Uri? contentUri = ParseContentUri(tileElement, contentLinks, sourceUri);

            return new Tile
            {
                Id = displayLabel,
                StablePath = stablePath,
                BoundingVolume = bounding,
                Transform = transform,
                ContentUri = contentUri,
                Children = children
            };
        }

        private static char EncodeDisplayIdSegment(int index, ParseContext context)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            if (index >= 16 && !context.HexWrapWarningIssued)
            {
                context.HexWrapWarningIssued = true;
                Trace.TraceWarning(
                    "3D Tiles sibling count exceeded compact hex display label range. Display labels wrap modulo 16; stable paths remain unique.");
            }

            int normalized = index % 16;
            return normalized <= 9
                ? (char)('0' + normalized)
                : (char)('A' + (normalized - 10));
        }

        private static BoundingVolume? ParseBoundingVolume(JsonElement tileElement)
        {
            return !tileElement.TryGetProperty("boundingVolume", out JsonElement element) || element.ValueKind != JsonValueKind.Object
                ? null
                : new BoundingVolume
                {
                    Region = ParseDoubleArray(element, "region"),
                    Box = ParseDoubleArray(element, "box"),
                    Sphere = ParseDoubleArray(element, "sphere")
                };
        }

        private static List<double>? ParseDoubleArray(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var values = new List<double>();
            foreach (JsonElement item in element.EnumerateArray())
            {
                values.Add(item.GetDouble());
            }

            return values;
        }

        private static Uri? ParseContentUri(
            JsonElement tileElement,
            TileSourceContentLinkOptions contentLinks,
            Uri sourceUri)
        {
            if (!tileElement.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!content.TryGetProperty("uri", out JsonElement uriElement) && !content.TryGetProperty("url", out uriElement))
            {
                return null;
            }

            string? raw = uriElement.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            Uri uri = Uri.TryCreate(raw, UriKind.Absolute, out Uri? absolute)
                ? absolute
                : new Uri(sourceUri, raw);

            Uri normalized = NormalizeContentUri(uri, contentLinks.FileSchemeBaseUri);
            return InheritRequiredQueryParameters(normalized, sourceUri, contentLinks.InheritedQueryParameters);
        }

        private static Uri NormalizeContentUri(Uri uri, Uri? fileSchemeBaseUri)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) ||
                fileSchemeBaseUri is null)
            {
                return uri;
            }

            string decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
            var builder = new UriBuilder(fileSchemeBaseUri)
            {
                Path = decodedPath,
                Query = uri.Query.TrimStart('?')
            };

            if (decodedPath.Contains('?', StringComparison.Ordinal))
            {
                int index = decodedPath.IndexOf('?', StringComparison.Ordinal);
                builder.Path = decodedPath[..index];
                builder.Query = decodedPath[(index + 1)..];
            }

            return builder.Uri;
        }

        private static Uri InheritRequiredQueryParameters(
            Uri targetUri,
            Uri sourceUri,
            IReadOnlyList<string> inheritedQueryParameters)
        {
            NameValueCollection sourceQuery = HttpUtility.ParseQueryString(sourceUri.Query);
            NameValueCollection targetQuery = HttpUtility.ParseQueryString(targetUri.Query);
            bool changed = false;

            foreach (string requiredKey in inheritedQueryParameters)
            {
                if (string.IsNullOrWhiteSpace(requiredKey))
                {
                    continue;
                }

                string key = requiredKey.Trim();
                string? sourceValue = sourceQuery[key];
                if (string.IsNullOrWhiteSpace(sourceValue))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(targetQuery[key]))
                {
                    continue;
                }

                targetQuery[key] = sourceValue;
                changed = true;
            }

            if (!changed)
            {
                return targetUri;
            }

            var builder = new UriBuilder(targetUri)
            {
                Query = targetQuery.ToString() ?? string.Empty
            };

            return builder.Uri;
        }

        private sealed class ParseContext
        {
            public bool HexWrapWarningIssued { get; set; }
        }
    }
}
