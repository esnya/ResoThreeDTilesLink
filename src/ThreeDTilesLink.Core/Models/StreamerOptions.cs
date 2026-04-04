namespace ThreeDTilesLink.Core.Models
{
    public sealed record StreamerOptions(
        GeoReference Reference,
        double HalfWidthM,
        string LinkHost,
        int LinkPort,
        int MaxTiles,
        int MaxDepth,
        double DetailTargetM,
        bool DryRun,
        string? ApiKey,
        double RenderStartSpanRatio = 4d,
        bool ManageResoniteConnection = true,
        string? MeshParentSlotId = null);
}
