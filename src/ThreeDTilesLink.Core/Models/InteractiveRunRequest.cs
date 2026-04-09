namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveRunRequest(
        string ResoniteHost,
        int ResonitePort,
        double HeightOffset,
        TraversalOptions Traversal,
        string? ApiKey,
        bool RemoveOutOfRange,
        WatchOptions Watch);
}
