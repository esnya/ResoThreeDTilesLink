using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts;

public interface IResoniteLinkClient
{
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
    Task SendTileMeshAsync(TileMeshPayload payload, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}
