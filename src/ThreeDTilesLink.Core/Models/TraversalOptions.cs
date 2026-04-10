namespace ThreeDTilesLink.Core.Models
{
    internal sealed record TraversalOptions(
        double RangeM,
        double DetailTargetM,
        double BootstrapRangeMultiplier = 4d);
}
