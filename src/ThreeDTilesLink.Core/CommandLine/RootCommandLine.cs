namespace ThreeDTilesLink.Core.CommandLine
{
    internal enum RootCommandKind
    {
        Stream,
        Interactive
    }

    internal sealed record RootCommandRoute(
        RootCommandKind Command,
        IReadOnlyList<string> Arguments);

    internal static class RootCommandLine
    {
        private const string Usage = "dotnet run --project src/ThreeDTilesLink -- <command> [options]";

        internal static CommandInvocation<RootCommandRoute> Parse(IReadOnlyList<string> args)
        {
            ArgumentNullException.ThrowIfNull(args);

            if (args.Count == 0)
            {
                return Error("Missing command.");
            }

            string command = args[0];
            if (string.Equals(command, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "-h", StringComparison.OrdinalIgnoreCase))
            {
                return new CommandInvocation<RootCommandRoute>(false, default, 0, RenderHelp(), false);
            }

            RootCommandKind? kind = command switch
            {
                _ when string.Equals(command, "stream", StringComparison.OrdinalIgnoreCase) => RootCommandKind.Stream,
                _ when string.Equals(command, "interactive", StringComparison.OrdinalIgnoreCase) => RootCommandKind.Interactive,
                _ => null
            };

            if (kind is null)
            {
                return Error($"Unknown command: {command}");
            }

            return new CommandInvocation<RootCommandRoute>(
                true,
                new RootCommandRoute(kind.Value, args.Skip(1).ToArray()),
                0,
                string.Empty,
                false);
        }

        internal static string RenderHelp()
        {
            string[] lines =
            [
                "Usage:",
                $"  {Usage}",
                string.Empty,
                "Commands:",
                "  stream       Fetch Google Photorealistic 3D Tiles around a center point and stream them to Resonite Link.",
                "  interactive  Attach watch variables in Resonite and keep streaming tiles as the selection input values change.",
                string.Empty,
                "Run '<command> --help' for command-specific options."
            ];

            return string.Join(Environment.NewLine, lines);
        }

        private static CommandInvocation<RootCommandRoute> Error(string message)
        {
            return new CommandInvocation<RootCommandRoute>(
                false,
                default,
                1,
                $"{message}{Environment.NewLine}{Environment.NewLine}{RenderHelp()}",
                true);
        }
    }
}
