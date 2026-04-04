namespace ThreeDTilesLink.Core.Models
{
    public sealed record TraversalOptions(
        double RangeM,
        int MaxTiles,
        int MaxDepth,
        double DetailTargetM,
        double BootstrapRangeMultiplier = 4d);
}
