using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.App;

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
        int ResoniteSendWorkers,
        int TimeoutSec,
        bool DryRun,
        LogLevel LogLevel) : ICommandRuntimeOptions;

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
                CommonCommandOptions.ResoniteSendWorkers("Maximum number of parallel Resonite send workers."),
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
            if (CommandInvocationBuilder.TryHandleParseResult(parsed, out CommandInvocation<StreamCommandOptions> handled))
            {
                return handled;
            }

            if (!CommandInvocationBuilder.TryGetLogLevel(parsed, out LogLevel logLevel, out string? logLevelRaw))
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>($"Invalid value for --log-level: {logLevelRaw}", RenderHelp);
            }

            if (!CommandInvocationBuilder.TryGetPositiveInt(parsed, "--content-workers", out int contentWorkers))
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>($"Invalid value for --content-workers: {contentWorkers}", RenderHelp);
            }

            if (!CommandInvocationBuilder.TryGetPositiveInt(parsed, "--resonite-send-workers", out int resoniteSendWorkers))
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>($"Invalid value for --resonite-send-workers: {resoniteSendWorkers}", RenderHelp);
            }

            if (!CommandInvocationBuilder.TryGetValue(parsed, "--latitude", out double latitude) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--longitude", out double longitude) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--height-offset", out double heightOffsetM) ||
                !CommandInvocationBuilder.TryGetPositiveDouble(parsed, "--range", out double rangeM) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--resonite-host", out string? resoniteHost) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--resonite-port", out int resonitePort) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--tile-limit", out int tileLimit) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--depth-limit", out int depthLimit) ||
                !CommandInvocationBuilder.TryGetPositiveDouble(parsed, "--detail", out double detailTargetM) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--timeout", out int timeoutSec) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--dry-run", out bool dryRun))
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>("Invalid command values.", RenderHelp);
            }

            if (string.IsNullOrWhiteSpace(resoniteHost))
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>("Invalid value for --resonite-host.", RenderHelp);
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
                resoniteSendWorkers,
                timeoutSec,
                dryRun,
                logLevel), 0, string.Empty, false);
        }

        internal static string RenderHelp()
        {
            return CommandLineParser.RenderHelp(Specification);
        }
    }
}
