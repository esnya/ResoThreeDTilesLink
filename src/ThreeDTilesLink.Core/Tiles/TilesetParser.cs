using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using System.Web;

namespace ThreeDTilesLink.Core.Tiles
{
    internal sealed class TilesetParser
    {
        public static Tileset Parse(string json, Uri sourceUri)
        {
            var context = new ParseContext();
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            return !root.TryGetProperty("root", out JsonElement rootTile)
                ? throw new InvalidOperationException("tileset root is missing.")
                : new Tileset(ParseTile(rootTile, sourceUri, "0", "0", context));
        }

        private static Tile ParseTile(
            JsonElement tileElement,
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
                        sourceUri,
                        $"{displayLabel}{EncodeDisplayIdSegment(index, context)}",
                        $"{stablePath}/{index}",
                        context));
                    index++;
                }
            }

            IReadOnlyList<double>? transform = ParseDoubleArray(tileElement, "transform");
            BoundingVolume? bounding = ParseBoundingVolume(tileElement);
            Uri? contentUri = ParseContentUri(tileElement, sourceUri);

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

        private static Uri? ParseContentUri(JsonElement tileElement, Uri sourceUri)
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

            Uri normalized = NormalizeGoogleContentUri(uri);
            return InheritRequiredQueryParameters(normalized, sourceUri);
        }

        private static Uri NormalizeGoogleContentUri(Uri uri)
        {
            // Google 3D Tiles may return content URIs using a file:// scheme,
            // but the actual content is served by tile.googleapis.com.
            if (!string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                return uri;
            }

            string decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
            if (decodedPath.Contains('?', StringComparison.Ordinal))
            {
                int index = decodedPath.IndexOf('?', StringComparison.Ordinal);
                string path = decodedPath[..index];
                string query = decodedPath[(index + 1)..];
                var builder = new UriBuilder("https", "tile.googleapis.com")
                {
                    Path = path,
                    Query = query
                };

                return builder.Uri;
            }

            return new UriBuilder("https", "tile.googleapis.com")
            {
                Path = decodedPath,
                Query = uri.Query.TrimStart('?')
            }.Uri;
        }

        private static Uri InheritRequiredQueryParameters(Uri targetUri, Uri sourceUri)
        {
            NameValueCollection sourceQuery = HttpUtility.ParseQueryString(sourceUri.Query);
            NameValueCollection targetQuery = HttpUtility.ParseQueryString(targetUri.Query);
            bool changed = false;

            foreach (string? requiredKey in new[] { "session" })
            {
                string? sourceValue = sourceQuery[requiredKey];
                if (string.IsNullOrWhiteSpace(sourceValue))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(targetQuery[requiredKey]))
                {
                    continue;
                }

                targetQuery[requiredKey] = sourceValue;
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
