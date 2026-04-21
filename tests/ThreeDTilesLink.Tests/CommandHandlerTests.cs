using FluentAssertions;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Tests
{
    public sealed class CommandHandlerTests
    {
        [Fact]
        public void StreamCreateRequest_MapsCommandOptionsToTileRunRequest()
        {
            var options = new StreamCommandOptions(
                35.65858d,
                139.745433d,
                20d,
                400d,
                "localhost",
                12000,
                25d,
                3,
                4,
                90,
                false,
                true,
                LogLevel.Debug);
            var tileSource = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json"),
                new TileSourceAccess("key", null));

            var request = StreamCommandHandler.CreateRequest(options, tileSource, new FakeGeoReferenceResolver());

            _ = request.SelectionReference.Latitude.Should().Be(35.65858d);
            _ = request.SelectionReference.Longitude.Should().Be(139.745433d);
            _ = request.SelectionReference.Height.Should().Be(120d);
            _ = request.PlacementReference.Should().BeEquivalentTo(request.SelectionReference);
            _ = request.Traversal.RangeM.Should().Be(400d);
            _ = request.Traversal.DetailTargetM.Should().Be(25d);
            _ = request.Output.Host.Should().Be("localhost");
            _ = request.Output.Port.Should().Be(12000);
            _ = request.Output.DryRun.Should().BeTrue();
            _ = request.Source.Should().BeEquivalentTo(tileSource);
            _ = request.Source.Access.ApiKey.Should().Be("key");
        }

        [Fact]
        public void InteractiveCreateRequest_MapsCommandOptionsToInteractiveRunRequest()
        {
            var options = new InteractiveCommandOptions(
                20d,
                "localhost",
                12000,
                25d,
                3,
                4,
                90,
                false,
                250,
                800,
                3000,
                LogLevel.Trace);
            var tileSource = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json"),
                new TileSourceAccess("key", null));

            var request = InteractiveCommandHandler.CreateRequest(options, tileSource, new SearchOptions("key"));

            _ = request.ResoniteHost.Should().Be("localhost");
            _ = request.ResonitePort.Should().Be(12000);
            _ = request.HeightOffset.Should().Be(20d);
            _ = request.Traversal.RangeM.Should().Be(0d);
            _ = request.Traversal.DetailTargetM.Should().Be(25d);
            _ = request.Search.ApiKey.Should().Be("key");
            _ = request.RemoveOutOfRange.Should().BeTrue();
            _ = request.Watch.PollInterval.Should().Be(TimeSpan.FromMilliseconds(250));
            _ = request.Watch.Debounce.Should().Be(TimeSpan.FromMilliseconds(800));
            _ = request.Watch.Throttle.Should().Be(TimeSpan.FromMilliseconds(3000));
        }

        private sealed class FakeGeoReferenceResolver : IGeoReferenceResolver
        {
            public GeoReference Resolve(double latitude, double longitude, double heightOffset)
            {
                return new GeoReference(latitude, longitude, 100d + heightOffset);
            }
        }
    }
}
