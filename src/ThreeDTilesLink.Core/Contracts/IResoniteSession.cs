using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface IResoniteSession
    {
        Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
        Task<string> CreateSessionChildSlotAsync(string name, CancellationToken cancellationToken);
        Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken);
        Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken);
        Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken);
        Task DisconnectAsync(CancellationToken cancellationToken);
    }
}
