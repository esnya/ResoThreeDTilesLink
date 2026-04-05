namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveRunRequest(
        string ResoniteHost,
        int ResonitePort,
        double HeightOffsetM,
        TraversalOptions Traversal,
        bool DryRun,
        string? ApiKey,
        bool RemoveOutOfRange,
        WatchOptions Watch);
}
