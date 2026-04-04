using System.Text.Json;
using System.Web;

namespace ThreeDTilesLink.Core.Tiles;

public sealed class TilesetParser
{
    public Tileset Parse(string json, Uri sourceUri)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("root", out var rootTile))
        {
            throw new InvalidOperationException("tileset root is missing.");
        }

        return new Tileset(
            ParseTile(rootTile, sourceUri, "0"),
            ParseCopyrights(root));
    }

    private static Tile ParseTile(JsonElement tileElement, Uri sourceUri, string id)
    {
        var children = new List<Tile>();
        if (tileElement.TryGetProperty("children", out var childrenElement) && childrenElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in childrenElement.EnumerateArray())
            {
                children.Add(ParseTile(child, sourceUri, $"{id}{EncodeIdSegment(index)}"));
                index++;
            }
        }

        var transform = ParseDoubleArray(tileElement, "transform");
        var bounding = ParseBoundingVolume(tileElement);
        var contentUri = ParseContentUri(tileElement, sourceUri);

        return new Tile
        {
            Id = id,
            BoundingVolume = bounding,
            Transform = transform,
            ContentUri = contentUri,
            Children = children
        };
    }

    private static char EncodeIdSegment(int index)
    {
        if (index <= 9)
        {
            return (char)('0' + index);
        }

        // Keep IDs compact: 10=>A ... 35=>Z, then loop A..Z.
        return (char)('A' + ((index - 10) % 26));
    }

    private static BoundingVolume? ParseBoundingVolume(JsonElement tileElement)
    {
        if (!tileElement.TryGetProperty("boundingVolume", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new BoundingVolume
        {
            Region = ParseDoubleArray(element, "region"),
            Box = ParseDoubleArray(element, "box"),
            Sphere = ParseDoubleArray(element, "sphere")
        };
    }

    private static IReadOnlyList<double>? ParseDoubleArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<double>();
        foreach (var item in element.EnumerateArray())
        {
            values.Add(item.GetDouble());
        }

        return values;
    }

    private static Uri? ParseContentUri(JsonElement tileElement, Uri sourceUri)
    {
        if (!tileElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!content.TryGetProperty("uri", out var uriElement) && !content.TryGetProperty("url", out uriElement))
        {
            return null;
        }

        var raw = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var uri = Uri.TryCreate(raw, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(sourceUri, raw);

        var normalized = NormalizeGoogleContentUri(uri);
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

        var decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
        if (decodedPath.Contains('?'))
        {
            var index = decodedPath.IndexOf('?', StringComparison.Ordinal);
            var path = decodedPath[..index];
            var query = decodedPath[(index + 1)..];
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
        var sourceQuery = HttpUtility.ParseQueryString(sourceUri.Query);
        var targetQuery = HttpUtility.ParseQueryString(targetUri.Query);
        var changed = false;

        foreach (var requiredKey in new[] { "session" })
        {
            var sourceValue = sourceQuery[requiredKey];
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

    private static IReadOnlyList<string> ParseCopyrights(JsonElement root)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectCopyright(root, output, seen);
        return output;
    }

    private static void CollectCopyright(
        JsonElement element,
        List<string> output,
        HashSet<string> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("copyright"))
                    {
                        AddCopyrightValue(property.Value, output, seen);
                        continue;
                    }

                    CollectCopyright(property.Value, output, seen);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectCopyright(item, output, seen);
                }

                break;
        }
    }

    private static void AddCopyrightValue(
        JsonElement value,
        List<string> output,
        HashSet<string> seen)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            AddCopyrightString(value.GetString(), output, seen);
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddCopyrightString(item.GetString(), output, seen);
            }
        }
    }

    private static void AddCopyrightString(
        string? raw,
        List<string> output,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var normalized = raw.Trim();
        if (seen.Add(normalized))
        {
            output.Add(normalized);
        }
    }
}
