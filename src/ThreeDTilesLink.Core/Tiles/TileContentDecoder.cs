using System.Text;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles
{
    internal sealed class TileContentDecoder(
        ITilesetParser tilesetParser,
        IB3dmGlbExtractor b3dmGlbExtractor) : ITileContentDecoder
    {
        private readonly ITilesetParser _tilesetParser = tilesetParser;
        private readonly IB3dmGlbExtractor _b3dmGlbExtractor = b3dmGlbExtractor;

        public FetchedNodeContent Decode(Uri contentUri, byte[] contentBytes, TileSourceOptions source, Uri sourceUri)
        {
            ArgumentNullException.ThrowIfNull(contentUri);
            ArgumentNullException.ThrowIfNull(contentBytes);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(sourceUri);

            return DetectKind(contentUri, contentBytes) switch
            {
                TileContentKind.Json => new NestedTilesetFetchedContent(
                    _tilesetParser.Parse(
                        Encoding.UTF8.GetString(contentBytes),
                        source.ContentLinks,
                        sourceUri)),
                TileContentKind.Glb => new GlbFetchedContent(contentBytes),
                TileContentKind.B3dm => new GlbFetchedContent(_b3dmGlbExtractor.ExtractGlb(contentBytes)),
                _ => new UnsupportedFetchedContent($"Unsupported tile content URI: {contentUri}")
            };
        }

        private static TileContentKind DetectKind(Uri contentUri, byte[] contentBytes)
        {
            string path = contentUri.AbsolutePath;
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return TileContentKind.Json;
            }

            if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            {
                return TileContentKind.Glb;
            }

            if (path.EndsWith(".b3dm", StringComparison.OrdinalIgnoreCase))
            {
                return TileContentKind.B3dm;
            }

            ReadOnlySpan<byte> span = contentBytes;
            if (span.Length >= 4)
            {
                if (span[..4].SequenceEqual("glTF"u8))
                {
                    return TileContentKind.Glb;
                }

                if (span[..4].SequenceEqual("b3dm"u8))
                {
                    return TileContentKind.B3dm;
                }
            }

            int firstNonWhitespace = Array.FindIndex(contentBytes, static value => !char.IsWhiteSpace((char)value));
            return firstNonWhitespace >= 0 && contentBytes[firstNonWhitespace] == (byte)'{'
                ? TileContentKind.Json
                : TileContentKind.Other;
        }
    }
}
