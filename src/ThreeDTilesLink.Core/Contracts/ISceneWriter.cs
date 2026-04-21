using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ISceneWriter
    {
        Task<string?> StreamMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken);
        Task RemoveNodeAsync(string nodeId, CancellationToken cancellationToken);
    }
}
