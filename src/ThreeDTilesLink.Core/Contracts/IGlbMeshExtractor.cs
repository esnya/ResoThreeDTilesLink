using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts;

public interface IGlbMeshExtractor
{
    GlbExtractResult Extract(byte[] glbBytes);
}
