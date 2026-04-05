using Microsoft.Extensions.Logging;

namespace ThreeDTilesLink.Core.CommandLine
{
    public sealed record StreamCommandOptions(
        double Latitude,
        double Longitude,
        double HeightOffsetM,
        double RangeM,
        string ResoniteHost,
        int ResonitePort,
        int TileLimit,
        int DepthLimit,
        double DetailTargetM,
        int ContentWorkers,
        int TimeoutSec,
        bool DryRun,
        LogLevel LogLevel);

    public static class StreamCommandLine
    {
        private static readonly CommandSpecification Specification = new(
            "dotnet run --project src/ThreeDTilesLink -- stream [options]",
            "Fetch Google Photorealistic 3D Tiles around a center point and stream them to Resonite Link.",
            [
                new("--latitude", CommandOptionValueKind.DecimalNumber, "Center latitude.", Required: true, Unit: "degrees"),
                new("--longitude", CommandOptionValueKind.DecimalNumber, "Center longitude.", Required: true, Unit: "degrees"),
                CommonCommandOptions.HeightOffset(),
                new("--range", CommandOptionValueKind.DecimalNumber, "Minimum coverage range from the center.", Required: true, Unit: "m", RenamedFrom: ["--half-width-m"]),
                CommonCommandOptions.ResoniteHost(),
                CommonCommandOptions.ResonitePort(),
                CommonCommandOptions.TileLimit("Maximum number of tiles to stream."),
                CommonCommandOptions.DepthLimit("Maximum traversal depth."),
                CommonCommandOptions.DetailTarget(),
                CommonCommandOptions.ContentWorkers("Maximum number of tile content fetch/decode workers."),
                CommonCommandOptions.Timeout(),
                CommonCommandOptions.DryRun(),
                CommonCommandOptions.LogLevelOption()
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--lat"] = "--lat was renamed to --latitude.",
                ["--lon"] = "--lon was renamed to --longitude.",
                ["--render-start-span-ratio"] = "--render-start-span-ratio is no longer supported."
            });

        public static CommandInvocation<StreamCommandOptions> Parse(IReadOnlyList<string> args)
        {
            ParsedCommand parsed = CommandLineParser.Parse(Specification, args);
            if (parsed.Status == CommandParseStatus.Help)
            {
                return new CommandInvocation<StreamCommandOptions>(false, default, 0, parsed.Output, false);
            }

            if (parsed.Status == CommandParseStatus.Error)
            {
                return new CommandInvocation<StreamCommandOptions>(false, default, 1, parsed.Output, true);
            }

            try
            {
                string logLevelRaw = (string)parsed.Values["--log-level"]!;
                if (!Enum.TryParse(logLevelRaw, ignoreCase: true, out LogLevel logLevel))
                {
                    return Error($"Invalid value for --log-level: {logLevelRaw}");
                }

                int contentWorkers = (int)parsed.Values["--content-workers"]!;
                if (contentWorkers <= 0)
                {
                    return Error($"Invalid value for --content-workers: {contentWorkers}");
                }

                return new CommandInvocation<StreamCommandOptions>(true, new StreamCommandOptions(
                    (double)parsed.Values["--latitude"]!,
                    (double)parsed.Values["--longitude"]!,
                    (double)parsed.Values["--height-offset"]!,
                    (double)parsed.Values["--range"]!,
                    (string)parsed.Values["--resonite-host"]!,
                    (int)parsed.Values["--resonite-port"]!,
                    (int)parsed.Values["--tile-limit"]!,
                    (int)parsed.Values["--depth-limit"]!,
                    (double)parsed.Values["--detail"]!,
                    contentWorkers,
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

        private static CommandInvocation<StreamCommandOptions> Error(string message)
        {
            return new CommandInvocation<StreamCommandOptions>(
                false,
                default,
                1,
                $"{message}{Environment.NewLine}{Environment.NewLine}{RenderHelp()}",
                true);
        }
    }
}
