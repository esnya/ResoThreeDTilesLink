using FluentAssertions;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.CommandLine;

namespace ThreeDTilesLink.Tests
{
    public sealed class StreamCommandLineTests
    {
        [Fact]
        public void Parse_Help_IncludesNewNamesUnitsAndDefaults()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(["--help"]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(0);
            _ = invocation.WriteToError.Should().BeFalse();
            _ = invocation.Output.Should().Contain("--latitude <value>");
            _ = invocation.Output.Should().Contain("--range <value>");
            _ = invocation.Output.Should().Contain("--content-workers <value>");
            _ = invocation.Output.Should().Contain("--resonite-send-workers <value>");
            _ = invocation.Output.Should().Contain("dotnet run --project src/ThreeDTilesLink -- stream [options]");
            _ = invocation.Output.Should().Contain("Default: localhost.");
            _ = invocation.Output.Should().Contain("Unit: m.");
            _ = invocation.Output.Should().Contain("Default: 120.");
            _ = invocation.Output.Should().Contain("Default: Information.");
        }

        [Fact]
        public void Parse_ShortHelp_IncludesHelpOutput()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(["-h"]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(0);
            _ = invocation.WriteToError.Should().BeFalse();
            _ = invocation.Output.Should().Contain("Usage:");
        }

        [Fact]
        public void Parse_AcceptsSeparatedAndEqualsForms()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
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
                "--resonite-send-workers", "5",
                "--timeout=90",
                "--dry-run",
                "--log-level", "Debug"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            StreamCommandOptions parsed = invocation.Options!;
            _ = parsed.Latitude.Should().Be(35.65858d);
            _ = parsed.Longitude.Should().Be(139.745433d);
            _ = parsed.HeightOffset.Should().Be(20d);
            _ = parsed.RangeM.Should().Be(400d);
            _ = parsed.ResoniteHost.Should().Be("127.0.0.1");
            _ = parsed.ResonitePort.Should().Be(12000);
            _ = parsed.TileLimit.Should().Be(128);
            _ = parsed.DepthLimit.Should().Be(16);
            _ = parsed.DetailTargetM.Should().Be(25d);
            _ = parsed.ContentWorkers.Should().Be(3);
            _ = parsed.ResoniteSendWorkers.Should().Be(5);
            _ = parsed.TimeoutSec.Should().Be(90);
            _ = parsed.DryRun.Should().BeTrue();
            _ = parsed.LogLevel.Should().Be(LogLevel.Debug);
        }

        [Fact]
        public void Parse_DefaultsResoniteHostToLocalhost()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
            [
                "--latitude", "35.65858",
                "--longitude", "139.745433",
                "--range", "400",
                "--resonite-port", "12000"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            StreamCommandOptions parsed = invocation.Options!;
            _ = parsed.ResoniteHost.Should().Be("localhost");
        }

        [Fact]
        public void Parse_DefaultsTileLimitTo2048()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
            [
                "--latitude", "35.65858",
                "--longitude", "139.745433",
                "--range", "400",
                "--resonite-port", "12000"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            StreamCommandOptions parsed = invocation.Options!;
            _ = parsed.TileLimit.Should().Be(2048);
        }

        [Fact]
        public void Parse_DryRun_DoesNotRequireResonitePort()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
            [
                "--latitude", "35.65858",
                "--longitude", "139.745433",
                "--range", "400",
                "--dry-run"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            StreamCommandOptions parsed = invocation.Options!;
            _ = parsed.DryRun.Should().BeTrue();
            _ = parsed.ResonitePort.Should().Be(0);
        }

        [Fact]
        public void Parse_RejectsRenamedArgument()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
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
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
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

        [Fact]
        public void Parse_RejectsNonPositiveResoniteSendWorkers()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
            [
                "--latitude", "35.0",
                "--longitude", "139.0",
                "--range", "400",
                "--resonite-port", "12000",
                "--resonite-send-workers", "0"
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.Output.Should().Contain("Invalid value for --resonite-send-workers: 0");
        }

        [Fact]
        public void Parse_RejectsInvalidLogLevel()
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
            [
                "--latitude", "35.0",
                "--longitude", "139.0",
                "--range", "400",
                "--resonite-port", "12000",
                "--log-level", "Verbose"
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.Output.Should().Contain("Invalid value for --log-level: Verbose");
        }

        [Theory]
        [InlineData("--range", "0")]
        [InlineData("--range", "-1")]
        [InlineData("--tile-limit", "0")]
        [InlineData("--depth-limit", "0")]
        [InlineData("--detail", "0")]
        [InlineData("--timeout", "-1")]
        public void Parse_RejectsInvalidPositiveNumericArguments(string option, string value)
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
            [
                "--latitude", "35.0",
                "--longitude", "139.0",
                "--range", "400",
                "--resonite-port", "12000",
                option, value
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.Output.Should().Contain("Invalid command values.");
        }

        [Theory]
        [InlineData("0")]
        [InlineData("65536")]
        public void Parse_RejectsInvalidResonitePort(string value)
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
            [
                "--latitude", "35.0",
                "--longitude", "139.0",
                "--range", "400",
                "--resonite-port", value
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.Output.Should().Contain("Invalid value for --resonite-port.");
        }

        [Theory]
        [InlineData("--latitude", "90.1")]
        [InlineData("--latitude", "-90.1")]
        [InlineData("--longitude", "180.1")]
        [InlineData("--longitude", "-180.1")]
        public void Parse_RejectsOutOfRangeCoordinates(string option, string value)
        {
            CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(
            [
                "--latitude", "35.0",
                "--longitude", "139.0",
                "--range", "400",
                "--resonite-port", "12000",
                option, value
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.Output.Should().Contain($"Invalid value for {option}: {value}");
        }
    }
}
