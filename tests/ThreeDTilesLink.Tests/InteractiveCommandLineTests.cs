using FluentAssertions;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.CommandLine;

namespace ThreeDTilesLink.Tests
{
    public sealed class InteractiveCommandLineTests
    {
        [Fact]
        public void Parse_Help_IncludesWatchAndTimingOptions()
        {
            CommandInvocation<InteractiveCommandOptions> invocation = InteractiveCommandLine.Parse(["--help"]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(0);
            _ = invocation.WriteToError.Should().BeFalse();
            _ = invocation.Output.Should().Contain("--poll-interval <value>");
            _ = invocation.Output.Should().Contain("--content-workers <value>");
            _ = invocation.Output.Should().Contain("--remove-out-of-range");
            _ = invocation.Output.Should().Contain("--watch-path <path>");
            _ = invocation.Output.Should().Contain("dotnet run --project src/ThreeDTilesLink -- interactive [options]");
            _ = invocation.Output.Should().Contain("Default: localhost.");
            _ = invocation.Output.Should().Contain("Unit: ms.");
            _ = invocation.Output.Should().Contain("Default: World/ThreeDTilesLink.");
        }

        [Fact]
        public void Parse_AcceptsNewVocabulary()
        {
            CommandInvocation<InteractiveCommandOptions> invocation = InteractiveCommandLine.Parse(
            [
                "--height-offset", "20",
                "--resonite-host", "127.0.0.1",
                "--resonite-port", "12000",
                "--tile-limit=128",
                "--depth-limit", "16",
                "--detail", "25",
                "--content-workers", "3",
                "--timeout", "90",
                "--poll-interval", "250",
                "--debounce=800",
                "--throttle", "3000",
                "--remove-out-of-range",
                "--watch-path", "World/ThreeDTilesLink/Watch/",
                "--dry-run",
                "--log-level", "Trace"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            InteractiveCommandOptions parsed = invocation.Options!;
            _ = parsed.HeightOffsetM.Should().Be(20d);
            _ = parsed.ContentWorkers.Should().Be(3);
            _ = parsed.PollIntervalMs.Should().Be(250);
            _ = parsed.DebounceMs.Should().Be(800);
            _ = parsed.ThrottleMs.Should().Be(3000);
            _ = parsed.RemoveOutOfRange.Should().BeTrue();
            _ = parsed.WatchPath.Should().Be("World/ThreeDTilesLink.Watch");
            _ = parsed.LogLevel.Should().Be(LogLevel.Trace);
        }

        [Fact]
        public void Parse_DefaultsResoniteHostToLocalhost()
        {
            CommandInvocation<InteractiveCommandOptions> invocation = InteractiveCommandLine.Parse(
            [
                "--resonite-port", "12000"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            InteractiveCommandOptions parsed = invocation.Options!;
            _ = parsed.ResoniteHost.Should().Be("localhost");
        }

        [Fact]
        public void Parse_DefaultsTileLimitTo2048()
        {
            CommandInvocation<InteractiveCommandOptions> invocation = InteractiveCommandLine.Parse(
            [
                "--resonite-port", "12000"
            ]);

            _ = invocation.ShouldRun.Should().BeTrue();
            InteractiveCommandOptions parsed = invocation.Options!;
            _ = parsed.TileLimit.Should().Be(2048);
        }

        [Fact]
        public void Parse_RejectsInteractiveRangeArgument()
        {
            CommandInvocation<InteractiveCommandOptions> invocation = InteractiveCommandLine.Parse(
            [
                "--range", "400",
                "--resonite-port", "12000"
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.Output.Should().Contain("--range is no longer supported in interactive mode.");
        }

        [Fact]
        public void Parse_RejectsNonPositiveContentWorkers()
        {
            CommandInvocation<InteractiveCommandOptions> invocation = InteractiveCommandLine.Parse(
            [
                "--resonite-port", "12000",
                "--content-workers", "0"
            ]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.Output.Should().Contain("Invalid value for --content-workers: 0");
        }
    }
}
