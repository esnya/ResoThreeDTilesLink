using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ICoordinateTransformer
    {
        Vector3d GeographicToEcef(double latitudeDeg, double longitudeDeg, double height);
        Vector3d EcefToEnu(Vector3d ecef, GeoReference reference);
        Vector3d EnuToEun(Vector3d enu);
    }
}
