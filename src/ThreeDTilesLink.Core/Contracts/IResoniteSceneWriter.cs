using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IResoniteSceneWriter
    {
        Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken);
        Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken);
    }
}
