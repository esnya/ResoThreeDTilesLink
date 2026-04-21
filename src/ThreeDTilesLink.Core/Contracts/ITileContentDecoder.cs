using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ITileContentDecoder
    {
        FetchedNodeContent Decode(Uri contentUri, byte[] contentBytes, TileSourceOptions source, Uri sourceUri);
    }
}
