using FluentAssertions;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Tests;

public sealed class CoordinateTransformerTests
{
    private readonly GeographicCoordinateTransformer _sut = new();

    [Fact]
    public void GeographicToEcef_EquatorPrimeMeridian_MapsToWgs84Radius()
    {
        var ecef = _sut.GeographicToEcef(0d, 0d, 0d);

        ecef.X.Should().BeApproximately(6378137d, 1e-3);
        ecef.Y.Should().BeApproximately(0d, 1e-6);
        ecef.Z.Should().BeApproximately(0d, 1e-6);
    }

    [Fact]
    public void EcefToEnu_AtReferencePoint_IsApproximatelyZero()
    {
        var reference = new GeoReference(35.65858, 139.745433, 20d);
        var ecef = _sut.GeographicToEcef(reference.Latitude, reference.Longitude, reference.HeightM);

        var enu = _sut.EcefToEnu(ecef, reference);

        enu.X.Should().BeApproximately(0d, 0.01d);
        enu.Y.Should().BeApproximately(0d, 0.01d);
        enu.Z.Should().BeApproximately(0d, 0.01d);
    }

    [Fact]
    public void EnuToEun_ReordersAxesWithoutTranslationMix()
    {
        var eun = _sut.EnuToEun(new Vector3d(12d, -3d, 7d));

        eun.X.Should().Be(12d);
        eun.Y.Should().Be(7d);
        eun.Z.Should().Be(-3d);
    }
}
