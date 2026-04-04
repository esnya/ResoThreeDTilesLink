namespace ThreeDTilesLink.Core.Models
{
    public sealed record TileRunRequest(
        GeoReference SelectionReference,
        GeoReference PlacementReference,
        TraversalOptions Traversal,
        ResoniteOutputOptions Output,
        string? ApiKey);
}
