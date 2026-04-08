using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ISelectedTileProjector
    {
        Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
        Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken);
        Task SetProgressAsync(string? parentSlotId, float progress01, string progressText, CancellationToken cancellationToken);
        Task SetProgressValueAsync(string? parentSlotId, float progress01, CancellationToken cancellationToken);
        Task SetProgressTextAsync(string? parentSlotId, string progressText, CancellationToken cancellationToken);
        Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken);
        Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken);
        Task DisconnectAsync(CancellationToken cancellationToken);
    }
}
