using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles
{
    internal static class TileContentClassifier
    {
        public static TileContentKind Classify(Uri contentUri)
        {
            ArgumentNullException.ThrowIfNull(contentUri);

            string path = contentUri.AbsolutePath;
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return TileContentKind.Json;
            }

            if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            {
                return TileContentKind.Glb;
            }

            return path.EndsWith(".b3dm", StringComparison.OrdinalIgnoreCase)
                ? TileContentKind.B3dm
                : TileContentKind.Other;
        }
    }
}
