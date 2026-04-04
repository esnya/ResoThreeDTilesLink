namespace ThreeDTilesLink.Core.Models
{
    public sealed record TileRunRequest(
        GeoReference Reference,
        TraversalOptions Traversal,
        ResoniteOutputOptions Output,
        string? ApiKey);
}
