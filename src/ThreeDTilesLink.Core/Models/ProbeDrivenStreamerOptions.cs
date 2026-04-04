namespace ThreeDTilesLink.Core.Models
{
    public sealed record ProbeDrivenStreamerOptions(
        string ResoniteHost,
        int ResonitePort,
        double HeightOffsetM,
        int MaxTiles,
        int MaxDepth,
        double DetailTargetM,
        bool DryRun,
        string? ApiKey,
        double BootstrapRangeMultiplier,
        TimeSpan PollInterval,
        TimeSpan Debounce,
        TimeSpan Throttle,
        ProbeConfiguration Probe);
}
