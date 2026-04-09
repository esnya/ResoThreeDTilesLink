namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IResoniteSessionMetadataPort
    {
        Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken);
        Task SetProgressAsync(string? parentSlotId, float progress01, string progressText, CancellationToken cancellationToken);
        Task SetProgressValueAsync(string? parentSlotId, float progress01, CancellationToken cancellationToken);
        Task SetProgressTextAsync(string? parentSlotId, string progressText, CancellationToken cancellationToken);
    }
}
