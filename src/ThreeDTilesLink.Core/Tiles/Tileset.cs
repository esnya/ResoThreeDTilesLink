namespace ThreeDTilesLink.Core.Tiles
{
    /// <summary>
    /// Parsed root tileset metadata.
    /// </summary>
    /// <param name="Root">Root tile in the hierarchy.</param>
    internal sealed record Tileset(Tile Root);

    /// <summary>
    /// Describes a single 3D Tiles node.
    /// </summary>
    internal sealed class Tile
    {
        /// <summary>
        /// Internal display label for the node.
        /// </summary>
        public string Id { get; init; } = string.Empty;
        /// <summary>
        /// Stable hierarchy path for internal identity.
        /// </summary>
        public string StablePath { get; init; } = string.Empty;
        /// <summary>
        /// Node local bounding volume.
        /// </summary>
        public BoundingVolume? BoundingVolume { get; init; }
        /// <summary>
        /// Flattened 4x4 transform matrix.
        /// </summary>
        public IReadOnlyList<double>? Transform { get; init; }
        /// <summary>
        /// URI to tile content.
        /// </summary>
        public Uri? ContentUri { get; init; }
        /// <summary>
        /// Child tiles.
        /// </summary>
        public IReadOnlyList<Tile> Children { get; init; } = [];
    }

    /// <summary>
    /// 3D Tiles bounding volume forms.
    /// </summary>
    internal sealed class BoundingVolume
    {
        /// <summary>
        /// Geodetic region volume, if present.
        /// </summary>
        public IReadOnlyList<double>? Region { get; init; }
        /// <summary>
        /// Oriented box volume, if present.
        /// </summary>
        public IReadOnlyList<double>? Box { get; init; }
        /// <summary>
        /// Bounding sphere, if present.
        /// </summary>
        public IReadOnlyList<double>? Sphere { get; init; }
    }
}
