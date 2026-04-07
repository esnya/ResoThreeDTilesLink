using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IGeoReferenceResolver
    {
        GeoReference Resolve(double latitude, double longitude, double heightOffset);
    }
}
