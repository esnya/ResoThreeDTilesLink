using FluentAssertions;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Tests
{
    public sealed class CoordinateTransformerTests
    {
        private readonly GeographicCoordinateTransformer _sut = new();

        [Fact]
        public void GeographicToEcef_EquatorPrimeMeridian_MapsToWgs84Radius()
        {
            Vector3d ecef = _sut.GeographicToEcef(0d, 0d, 0d);

            _ = ecef.X.Should().BeApproximately(6378137d, 1e-3);
            _ = ecef.Y.Should().BeApproximately(0d, 1e-6);
            _ = ecef.Z.Should().BeApproximately(0d, 1e-6);
        }

        [Fact]
        public void EcefToEnu_AtReferencePoint_IsApproximatelyZero()
        {
            var reference = new GeoReference(35.65858, 139.745433, 20d);
            Vector3d ecef = _sut.GeographicToEcef(reference.Latitude, reference.Longitude, reference.Height);

            Vector3d enu = _sut.EcefToEnu(ecef, reference);

            _ = enu.X.Should().BeApproximately(0d, 0.01d);
            _ = enu.Y.Should().BeApproximately(0d, 0.01d);
            _ = enu.Z.Should().BeApproximately(0d, 0.01d);
        }

        [Fact]
        public void EnuToEun_ReordersAxesWithoutTranslationMix()
        {
            Vector3d eun = _sut.EnuToEun(new Vector3d(12d, -3d, 7d));

            _ = eun.X.Should().Be(12d);
            _ = eun.Y.Should().Be(7d);
            _ = eun.Z.Should().Be(-3d);
        }
    }
}
