using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts;

public interface IResoniteLinkClient
{
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
    Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken);
    Task<string?> SendTileMeshAsync(TileMeshPayload payload, CancellationToken cancellationToken);
    Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}
