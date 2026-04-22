namespace ThreeDTilesLink.Core.Models
{
    internal sealed record TileRunRequest(
        GeoReference SelectionReference,
        GeoReference PlacementReference,
        TraversalOptions Traversal,
        SceneOutputOptions Output,
        TileSourceOptions Source);
}
