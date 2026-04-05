using FluentAssertions;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.CommandLine;

namespace ThreeDTilesLink.Tests
{
    public sealed class CliCommandLineTests
    {
        [Fact]
        public void Parse_Help_IncludesNewNamesUnitsAndDefaults()
        {
            CommandInvocation<CliCommandOptions> invocation = CliCommandLine.Parse(["--help"]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(0);
            _ = invocation.WriteToError.Should().BeFalse();
            _ = invocation.Output.Should().Contain("--latitude <value>");
            _ = invocation.Output.Should().Contain("--range <value>");
            _ = invocation.Output.Should().Contain("--content-workers <value>");
            _ = invocation.Output.Should().Contain("Default: localhost.");
            _ = invocation.Output.Should().Contain("Unit: m.");
            _ = invocation.Output.Should().Contain("Default: 120.");
            _ = invocation.Output.Should().Contain("Default: Information.");
        }

        [Fact]
        public void Parse_AcceptsSeparatedAndEqualsForms()
        {
            CommandInvocation<CliCommandOptions> invocation = CliCommandLine.Parse(
            [
                "--latitude=35.65858",
                "--longitude", "139.745433",
                "--height-offset", "20",
                "--range=400",
                "--resonite-host", "127.0.0.1",
                "--resonite-port=12000",
                "--tile-limit", "128",
                "--depth-limit=16",
                "--detail", "25",
                "--content-workers", "3",
                "--timeout=90",
                "--dry-run",
                "--log-level", "Debug"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            CliCommandOptions parsed = invocation.Options!;
            _ = parsed.Latitude.Should().Be(35.65858d);
            _ = parsed.Longitude.Should().Be(139.745433d);
            _ = parsed.HeightOffsetM.Should().Be(20d);
            _ = parsed.RangeM.Should().Be(400d);
            _ = parsed.ResoniteHost.Should().Be("127.0.0.1");
            _ = parsed.ResonitePort.Should().Be(12000);
            _ = parsed.TileLimit.Should().Be(128);
            _ = parsed.DepthLimit.Should().Be(16);
            _ = parsed.DetailTargetM.Should().Be(25d);
            _ = parsed.ContentWorkers.Should().Be(3);
            _ = parsed.TimeoutSec.Should().Be(90);
            _ = parsed.DryRun.Should().BeTrue();
            _ = parsed.LogLevel.Should().Be(LogLevel.Debug);
        }

        [Fact]
        public void Parse_DefaultsResoniteHostToLocalhost()
        {
            CommandInvocation<CliCommandOptions> invocation = CliCommandLine.Parse(
            [
                "--latitude", "35.65858",
                "--longitude", "139.745433",
                "--range", "400",
                "--resonite-port", "12000"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            CliCommandOptions parsed = invocation.Options!;
            _ = parsed.ResoniteHost.Should().Be("localhost");
        }

        [Fact]
        public void Parse_RejectsRenamedArgument()
        {
            CommandInvocation<CliCommandOptions> invocation = CliCommandLine.Parse(
            [
                "--latitude", "35.0",
                "--longitude", "139.0",
                "--half-width-m", "400",
                "--resonite-port", "12000"
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.WriteToError.Should().BeTrue();
            _ = invocation.Output.Should().Contain("--half-width-m was renamed to --range.");
        }

        [Fact]
        public void Parse_RejectsNonPositiveContentWorkers()
        {
            CommandInvocation<CliCommandOptions> invocation = CliCommandLine.Parse(
            [
                "--latitude", "35.0",
                "--longitude", "139.0",
                "--range", "400",
                "--resonite-port", "12000",
                "--content-workers", "0"
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.Output.Should().Contain("Invalid value for --content-workers: 0");
        }
    }
}
