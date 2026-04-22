using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IMeshPlacementService
    {
        IReadOnlyList<PlacedMeshPayload> Place(
            TileSelectionResult tile,
            IReadOnlyList<MeshData> meshes,
            GeoReference reference,
            string? parentNodeId);
    }
}
