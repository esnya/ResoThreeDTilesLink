namespace ThreeDTilesLink.Core.Tiles;

public sealed record Tileset(Tile Root, IReadOnlyList<string>? Copyrights = null);

public sealed class Tile
{
    public string Id { get; init; } = string.Empty;
    public BoundingVolume? BoundingVolume { get; init; }
    public IReadOnlyList<double>? Transform { get; init; }
    public Uri? ContentUri { get; init; }
    public IReadOnlyList<Tile> Children { get; init; } = Array.Empty<Tile>();
}

public sealed class BoundingVolume
{
    public IReadOnlyList<double>? Region { get; init; }
    public IReadOnlyList<double>? Box { get; init; }
    public IReadOnlyList<double>? Sphere { get; init; }
}
