using GeographicLib;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Geo;

public sealed class GeographicCoordinateTransformer : ICoordinateTransformer
{
    private readonly Geocentric _earth = Geocentric.WGS84;

    public Vector3d GeographicToEcef(double latitudeDeg, double longitudeDeg, double heightM)
    {
        Span<double> rotation = stackalloc double[9];
        var (x, y, z) = _earth.Forward(latitudeDeg, longitudeDeg, heightM, rotation);
        return new Vector3d(x, y, z);
    }

    public Vector3d EcefToEnu(Vector3d ecef, GeoReference reference)
    {
        Span<double> reverseRotation = stackalloc double[9];
        var (lat, lon, h) = _earth.Reverse(ecef.X, ecef.Y, ecef.Z, reverseRotation);

        var local = new LocalCartesian(reference.Latitude, reference.Longitude, reference.HeightM, _earth);
        Span<double> localRotation = stackalloc double[9];
        var (east, north, up) = local.Forward(lat, lon, h, localRotation);

        return new Vector3d(east, north, up);
    }

    public Vector3d EnuToEun(Vector3d enu) => new(enu.X, enu.Z, enu.Y);
}
