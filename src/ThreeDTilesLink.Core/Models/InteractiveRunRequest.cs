namespace ThreeDTilesLink.Core.Models
{
    public sealed record InteractiveRunRequest(
        string ResoniteHost,
        int ResonitePort,
        double HeightOffsetM,
        TraversalOptions Traversal,
        bool DryRun,
        string? ApiKey,
        ProbeWatchOptions ProbeWatch);
}
