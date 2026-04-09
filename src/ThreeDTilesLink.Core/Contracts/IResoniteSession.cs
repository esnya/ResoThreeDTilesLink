using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IResoniteSession
    {
        Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
        Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken);
        Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken);
        Task DisconnectAsync(CancellationToken cancellationToken);
    }
}
