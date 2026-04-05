namespace ThreeDTilesLink.Core.Tiles
{
    /// <summary>
    /// Parsed root tileset metadata and optional asset copyrights.
    /// </summary>
    /// <param name="Root">Root tile in the hierarchy.</param>
    /// <param name="Copyrights">Optional asset-level copyright statements.</param>
    public sealed record Tileset(Tile Root, IReadOnlyList<string>? Copyrights = null);

    /// <summary>
    /// Describes a single 3D Tiles node.
    /// </summary>
    public sealed class Tile
    {
        /// <summary>
        /// Stable node identifier.
        /// </summary>
        public string Id { get; init; } = string.Empty;
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
    public sealed class BoundingVolume
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
