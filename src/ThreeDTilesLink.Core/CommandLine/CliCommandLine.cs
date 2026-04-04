using Microsoft.Extensions.Logging;

namespace ThreeDTilesLink.Core.CommandLine
{
    public sealed record CliCommandOptions(
        double Latitude,
        double Longitude,
        double HeightOffsetM,
        double RangeM,
        string ResoniteHost,
        int ResonitePort,
        int TileLimit,
        int DepthLimit,
        double DetailTargetM,
        int TimeoutSec,
        bool DryRun,
        LogLevel LogLevel);

    public static class CliCommandLine
    {
        private static readonly CommandSpecification Specification = new(
            "dotnet run --project src/ThreeDTilesLink.Cli -- [options]",
            "Fetch Google Photorealistic 3D Tiles around a center point and stream them to Resonite Link.",
            [
                new("--latitude", CommandOptionValueKind.DecimalNumber, "Center latitude.", Required: true, Unit: "degrees"),
                new("--longitude", CommandOptionValueKind.DecimalNumber, "Center longitude.", Required: true, Unit: "degrees"),
                new("--height-offset", CommandOptionValueKind.DecimalNumber, "Height offset applied to the reference point.", DefaultValue: 0d, Unit: "m", RenamedFrom: ["--height-offset-m"]),
                new("--range", CommandOptionValueKind.DecimalNumber, "Minimum coverage range from the center.", Required: true, Unit: "m", RenamedFrom: ["--half-width-m"]),
                new("--resonite-host", CommandOptionValueKind.Text, "Resonite Link host name or IP address.", Required: true, ValueName: "host", RenamedFrom: ["--link-host"]),
                new("--resonite-port", CommandOptionValueKind.WholeNumber, "Resonite Link port.", Required: true, ValueName: "port", RenamedFrom: ["--link-port"]),
                new("--tile-limit", CommandOptionValueKind.WholeNumber, "Maximum number of tiles to stream.", DefaultValue: 1024, RenamedFrom: ["--max-tiles"]),
                new("--depth-limit", CommandOptionValueKind.WholeNumber, "Maximum traversal depth.", DefaultValue: 32, RenamedFrom: ["--max-depth"]),
                new("--detail", CommandOptionValueKind.DecimalNumber, "Target tile detail before traversal stops descending renderable GLB tiles.", DefaultValue: 30d, Unit: "m", RenamedFrom: ["--detail-target-m"]),
                new("--timeout", CommandOptionValueKind.WholeNumber, "Request timeout.", DefaultValue: 120, Unit: "sec", RenamedFrom: ["--timeout-sec"]),
                new("--dry-run", CommandOptionValueKind.Switch, "Fetch and convert tiles without sending anything to Resonite.", DefaultValue: false),
                new("--log-level", CommandOptionValueKind.Text, "Logging level.", DefaultValue: "Information", ValueName: "level")
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--lat"] = "--lat was renamed to --latitude.",
                ["--lon"] = "--lon was renamed to --longitude.",
                ["--render-start-span-ratio"] = "--render-start-span-ratio is no longer supported."
            });

        public static CommandInvocation<CliCommandOptions> Parse(IReadOnlyList<string> args)
        {
            ParsedCommand parsed = CommandLineParser.Parse(Specification, args);
            if (parsed.Status == CommandParseStatus.Help)
            {
                return new CommandInvocation<CliCommandOptions>(false, default, 0, parsed.Output, false);
            }

            if (parsed.Status == CommandParseStatus.Error)
            {
                return new CommandInvocation<CliCommandOptions>(false, default, 1, parsed.Output, true);
            }

            try
            {
                string logLevelRaw = (string)parsed.Values["--log-level"]!;
                if (!Enum.TryParse(logLevelRaw, ignoreCase: true, out LogLevel logLevel))
                {
                    return Error($"Invalid value for --log-level: {logLevelRaw}");
                }

                return new CommandInvocation<CliCommandOptions>(true, new CliCommandOptions(
                    (double)parsed.Values["--latitude"]!,
                    (double)parsed.Values["--longitude"]!,
                    (double)parsed.Values["--height-offset"]!,
                    (double)parsed.Values["--range"]!,
                    (string)parsed.Values["--resonite-host"]!,
                    (int)parsed.Values["--resonite-port"]!,
                    (int)parsed.Values["--tile-limit"]!,
                    (int)parsed.Values["--depth-limit"]!,
                    (double)parsed.Values["--detail"]!,
                    (int)parsed.Values["--timeout"]!,
                    (bool)parsed.Values["--dry-run"]!,
                    logLevel), 0, string.Empty, false);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        public static string RenderHelp()
        {
            return CommandLineParser.RenderHelp(Specification);
        }

        private static CommandInvocation<CliCommandOptions> Error(string message)
        {
            return new CommandInvocation<CliCommandOptions>(
                false,
                default,
                1,
                $"{message}{Environment.NewLine}{Environment.NewLine}{RenderHelp()}",
                true);
        }
    }
}
