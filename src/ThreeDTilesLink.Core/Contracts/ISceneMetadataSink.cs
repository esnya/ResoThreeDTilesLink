namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ISceneMetadataSink
    {
        Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken);
        Task SetProgressAsync(string? parentNodeId, float progress01, string progressText, CancellationToken cancellationToken);
        Task SetProgressValueAsync(string? parentNodeId, float progress01, CancellationToken cancellationToken);
        Task SetProgressTextAsync(string? parentNodeId, string progressText, CancellationToken cancellationToken);
    }
}
