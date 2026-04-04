using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface IMeshPlacementService
    {
        IReadOnlyList<PlacedMeshPayload> Place(
            TileSelectionResult tile,
            IReadOnlyList<MeshData> meshes,
            GeoReference reference,
            string? parentSlotId);
    }
}
