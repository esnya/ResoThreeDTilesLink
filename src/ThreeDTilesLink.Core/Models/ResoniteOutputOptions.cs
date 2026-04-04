namespace ThreeDTilesLink.Core.Models
{
    public sealed record ResoniteOutputOptions(
        string Host,
        int Port,
        bool DryRun,
        bool ManageConnection = true,
        string? MeshParentSlotId = null);
}
