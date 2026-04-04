namespace ThreeDTilesLink.Core.Models
{
    public sealed record StreamerOptions(
        GeoReference Reference,
        double RangeM,
        string ResoniteHost,
        int ResonitePort,
        int MaxTiles,
        int MaxDepth,
        double DetailTargetM,
        bool DryRun,
        string? ApiKey,
        double BootstrapRangeMultiplier = 4d,
        bool ManageResoniteConnection = true,
        string? MeshParentSlotId = null);
}
