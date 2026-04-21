using FluentAssertions;
using ThreeDTilesLink.Core.Generic;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.Tests
{
    public sealed class LicenseCreditAggregatorTests
    {
        [Fact]
        public void ParseOwners_NormalizesGoogleToGoogleMaps_AndDeduplicates()
        {
            IReadOnlyList<string> owners = new LicenseCreditAggregator().ParseOwners(
            [
                "Google; Maxar Technologies",
                "Google Maps; Maxar Technologies"
            ]);

            _ = owners.Should().Equal("Google Maps", "Maxar Technologies");
        }

        [Fact]
        public void BuildCreditString_AlwaysKeepsGoogleMapsBasemapAttributionFirst()
        {
            var aggregator = new LicenseCreditAggregator();
            IReadOnlyList<string> owners = aggregator.ParseOwners(
            [
                "Airbus; Google"
            ]);

            aggregator.RegisterOrder(owners);
            _ = aggregator.Activate(owners);

            _ = aggregator.BuildCreditString().Should().Be("Google Maps; Airbus");
        }

        [Fact]
        public void BuildCreditString_DoesNotForceGoogleCreditForGenericPolicy()
        {
            var aggregator = new LicenseCreditAggregator(new GenericTileLicenseCreditPolicy());
            IReadOnlyList<string> owners = aggregator.ParseOwners(
            [
                "PLATEAU; City of Yokohama"
            ]);

            aggregator.RegisterOrder(owners);
            _ = aggregator.Activate(owners);

            _ = aggregator.BuildCreditString().Should().Be("PLATEAU; City of Yokohama");
        }
    }
}
