using FluentAssertions;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;

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
                128,
                16,
                25d,
                3,
                90,
                true,
                LogLevel.Debug);

            var request = StreamCommandHandler.CreateRequest(options, "key");

            _ = request.SelectionReference.Latitude.Should().Be(35.65858d);
            _ = request.SelectionReference.Longitude.Should().Be(139.745433d);
            _ = request.SelectionReference.HeightM.Should().Be(20d);
            _ = request.PlacementReference.Should().BeEquivalentTo(request.SelectionReference);
            _ = request.Traversal.RangeM.Should().Be(400d);
            _ = request.Traversal.MaxTiles.Should().Be(128);
            _ = request.Traversal.MaxDepth.Should().Be(16);
            _ = request.Traversal.DetailTargetM.Should().Be(25d);
            _ = request.Output.Host.Should().Be("localhost");
            _ = request.Output.Port.Should().Be(12000);
            _ = request.Output.DryRun.Should().BeTrue();
            _ = request.ApiKey.Should().Be("key");
        }

        [Fact]
        public void InteractiveCreateRequest_MapsCommandOptionsToInteractiveRunRequest()
        {
            var options = new InteractiveCommandOptions(
                20d,
                "localhost",
                12000,
                128,
                16,
                25d,
                3,
                90,
                250,
                800,
                3000,
                true,
                true,
                "3DTilesLink Watch",
                "World/ThreeDTilesLink.Watch",
                LogLevel.Trace);

            var request = InteractiveCommandHandler.CreateRequest(options, "key");

            _ = request.ResoniteHost.Should().Be("localhost");
            _ = request.ResonitePort.Should().Be(12000);
            _ = request.HeightOffsetM.Should().Be(20d);
            _ = request.Traversal.RangeM.Should().Be(0d);
            _ = request.Traversal.MaxTiles.Should().Be(128);
            _ = request.Traversal.MaxDepth.Should().Be(16);
            _ = request.Traversal.DetailTargetM.Should().Be(25d);
            _ = request.DryRun.Should().BeTrue();
            _ = request.ApiKey.Should().Be("key");
            _ = request.RemoveOutOfRange.Should().BeTrue();
            _ = request.Watch.PollInterval.Should().Be(TimeSpan.FromMilliseconds(250));
            _ = request.Watch.Debounce.Should().Be(TimeSpan.FromMilliseconds(800));
            _ = request.Watch.Throttle.Should().Be(TimeSpan.FromMilliseconds(3000));
            _ = request.Watch.Configuration.SlotName.Should().Be("3DTilesLink Watch");
            _ = request.Watch.Configuration.LatitudeVariablePath.Should().Be("World/ThreeDTilesLink.Watch.Latitude");
            _ = request.Watch.Configuration.LongitudeVariablePath.Should().Be("World/ThreeDTilesLink.Watch.Longitude");
            _ = request.Watch.Configuration.RangeVariablePath.Should().Be("World/ThreeDTilesLink.Watch.Range");
            _ = request.Watch.Configuration.SearchVariablePath.Should().Be("World/ThreeDTilesLink.Watch.Search");
        }
    }
}
