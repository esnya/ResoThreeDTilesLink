namespace ThreeDTilesLink.Core.Models
{
    public sealed record ProbeDrivenStreamerOptions(
        string LinkHost,
        int LinkPort,
        double HeightOffsetM,
        int MaxTiles,
        int MaxDepth,
        double DetailTargetM,
        bool DryRun,
        string? ApiKey,
        double RenderStartSpanRatio,
        TimeSpan PollInterval,
        TimeSpan Debounce,
        TimeSpan Throttle,
        ProbeConfiguration Probe);
}
