namespace ThreeDTilesLink.Core.Models
{
    internal sealed record ResoniteOutputOptions(
        string Host,
        int Port,
        bool DryRun,
        bool ManageConnection = true,
        string? MeshParentSlotId = null);
}
