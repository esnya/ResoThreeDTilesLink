using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles
{
    public static class TileContentClassifier
    {
        public static TileContentKind Classify(Uri contentUri)
        {
            ArgumentNullException.ThrowIfNull(contentUri);

            return contentUri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? TileContentKind.Json
                : contentUri.AbsolutePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                    ? TileContentKind.Glb
                    : TileContentKind.Other;
        }
    }
}
