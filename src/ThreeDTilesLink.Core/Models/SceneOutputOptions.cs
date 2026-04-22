namespace ThreeDTilesLink.Core.Models
{
    internal sealed record SceneOutputOptions(
        string Host,
        int Port,
        bool DryRun,
        bool ManageConnection = true,
        string? ParentNodeId = null);
}
