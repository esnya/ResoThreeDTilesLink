using Microsoft.Extensions.Logging;

namespace ThreeDTilesLink.Core.CommandLine
{
    internal sealed record StreamCommandOptions(
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

    internal static class StreamCommandLine
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

        internal static CommandInvocation<StreamCommandOptions> Parse(IReadOnlyList<string> args)
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

            if (!TryGetValue(parsed, "--log-level", out string? logLevelRaw) ||
                !Enum.TryParse(logLevelRaw, ignoreCase: true, out LogLevel logLevel))
            {
                return Error($"Invalid value for --log-level: {logLevelRaw}");
            }

            if (!TryGetValue(parsed, "--content-workers", out int contentWorkers) || contentWorkers <= 0)
            {
                return Error($"Invalid value for --content-workers: {contentWorkers}");
            }

            if (!TryGetValue(parsed, "--latitude", out double latitude) ||
                !TryGetValue(parsed, "--longitude", out double longitude) ||
                !TryGetValue(parsed, "--height-offset", out double heightOffsetM) ||
                !TryGetValue(parsed, "--range", out double rangeM) ||
                !TryGetValue(parsed, "--resonite-host", out string? resoniteHost) ||
                !TryGetValue(parsed, "--resonite-port", out int resonitePort) ||
                !TryGetValue(parsed, "--tile-limit", out int tileLimit) ||
                !TryGetValue(parsed, "--depth-limit", out int depthLimit) ||
                !TryGetValue(parsed, "--detail", out double detailTargetM) ||
                !TryGetValue(parsed, "--timeout", out int timeoutSec) ||
                !TryGetValue(parsed, "--dry-run", out bool dryRun))
            {
                return Error("Invalid command values.");
            }

            if (string.IsNullOrWhiteSpace(resoniteHost))
            {
                return Error("Invalid value for --resonite-host.");
            }

            return new CommandInvocation<StreamCommandOptions>(true, new StreamCommandOptions(
                latitude,
                longitude,
                heightOffsetM,
                rangeM,
                resoniteHost,
                resonitePort,
                tileLimit,
                depthLimit,
                detailTargetM,
                contentWorkers,
                timeoutSec,
                dryRun,
                logLevel), 0, string.Empty, false);
        }

        internal static string RenderHelp()
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

        private static bool TryGetValue<T>(ParsedCommand parsed, string key, out T value)
        {
            if (parsed.Values.TryGetValue(key, out object? raw) && raw is T typed)
            {
                value = typed;
                return true;
            }

            value = default!;
            return false;
        }
    }
}
