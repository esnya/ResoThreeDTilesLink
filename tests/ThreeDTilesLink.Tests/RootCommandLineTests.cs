using FluentAssertions;
using ThreeDTilesLink.Core.CommandLine;

namespace ThreeDTilesLink.Tests
{
    public sealed class RootCommandLineTests
    {
        [Fact]
        public void Parse_Help_IncludesSubcommandList()
        {
            CommandInvocation<RootCommandRoute> invocation = RootCommandLine.Parse(["--help"]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(0);
            _ = invocation.WriteToError.Should().BeFalse();
            _ = invocation.Output.Should().Contain("stream");
            _ = invocation.Output.Should().Contain("interactive");
        }

        [Fact]
        public void Parse_RoutesStreamSubcommand()
        {
            CommandInvocation<RootCommandRoute> invocation = RootCommandLine.Parse(["stream", "--help"]);

            _ = invocation.ShouldRun.Should().BeTrue();
            _ = invocation.Options!.Command.Should().Be(RootCommandKind.Stream);
            _ = invocation.Options.Arguments.Should().Equal("--help");
        }

        [Fact]
        public void Parse_RoutesInteractiveSubcommand()
        {
            CommandInvocation<RootCommandRoute> invocation = RootCommandLine.Parse(["interactive", "--resonite-port", "12000"]);

            _ = invocation.ShouldRun.Should().BeTrue();
            _ = invocation.Options!.Command.Should().Be(RootCommandKind.Interactive);
            _ = invocation.Options.Arguments.Should().Equal("--resonite-port", "12000");
        }

        [Fact]
        public void Parse_RejectsMissingCommand()
        {
            CommandInvocation<RootCommandRoute> invocation = RootCommandLine.Parse([]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.WriteToError.Should().BeTrue();
            _ = invocation.Output.Should().Contain("Missing command.");
        }

        [Fact]
        public void Parse_RejectsUnknownCommand()
        {
            CommandInvocation<RootCommandRoute> invocation = RootCommandLine.Parse(["cli"]);

            _ = invocation.ShouldRun.Should().BeFalse();
            _ = invocation.ExitCode.Should().Be(1);
            _ = invocation.WriteToError.Should().BeTrue();
            _ = invocation.Output.Should().Contain("Unknown command: cli");
        }
    }
}
