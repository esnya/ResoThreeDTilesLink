using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts;

public interface IGlbMeshExtractor
{
    IReadOnlyList<MeshData> Extract(byte[] glbBytes);
}
