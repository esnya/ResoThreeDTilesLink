using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IGlbMeshExtractor
    {
        GlbExtractResult Extract(byte[] glbBytes);
    }
}
