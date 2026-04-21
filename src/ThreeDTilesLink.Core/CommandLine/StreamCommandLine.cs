using System.Globalization;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.App;

namespace ThreeDTilesLink.Core.CommandLine
{
    internal sealed record StreamCommandOptions(
        double Latitude,
        double Longitude,
        double HeightOffset,
        double RangeM,
        string ResoniteHost,
        int ResonitePort,
        double DetailTargetM,
        int ContentWorkers,
        int ResoniteSendWorkers,
        int TimeoutSec,
        bool MeasurePerformance,
        bool DryRun,
        LogLevel LogLevel) : ICommandRuntimeOptions;

    internal static class StreamCommandLine
    {
        private static readonly CommandSpecification Specification = new(
            "dotnet run --project src/ThreeDTilesLink -- stream [options]",
            "Fetch 3D Tiles around a center point and stream them to Resonite Link.",
            [
                new("--latitude", CommandOptionValueKind.DecimalNumber, "Center latitude.", Required: true, Unit: "degrees"),
                new("--longitude", CommandOptionValueKind.DecimalNumber, "Center longitude.", Required: true, Unit: "degrees"),
                CommonCommandOptions.HeightOffset(),
                new("--range", CommandOptionValueKind.DecimalNumber, "Approximate square coverage half-width from the center (X/Z local extent).", Required: true, Unit: "m", RenamedFrom: ["--half-width-m"]),
                CommonCommandOptions.ResoniteHost(),
                CommonCommandOptions.ResonitePort(required: false),
                CommonCommandOptions.DetailTarget(),
                CommonCommandOptions.ContentWorkers("Maximum number of tile content fetch/decode workers."),
                CommonCommandOptions.ResoniteSendWorkers("Maximum number of parallel Resonite send workers."),
                CommonCommandOptions.MeasurePerformance(),
                CommonCommandOptions.Timeout(),
                new("--dry-run", CommandOptionValueKind.Switch, "Fetch and convert tiles without sending anything to Resonite.", DefaultValue: false),
                CommonCommandOptions.LogLevelOption()
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--lat"] = "--lat was renamed to --latitude.",
                ["--lon"] = "--lon was renamed to --longitude.",
                ["--tile-limit"] = "--tile-limit is no longer supported.",
                ["--max-tiles"] = "--max-tiles is no longer supported.",
                ["--depth-limit"] = "--depth-limit is no longer supported.",
                ["--max-depth"] = "--max-depth is no longer supported.",
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
                !CommandInvocationBuilder.TryGetValue(parsed, "--height-offset", out double heightOffset) ||
                !CommandInvocationBuilder.TryGetPositiveDouble(parsed, "--range", out double rangeM) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--resonite-host", out string? resoniteHost) ||
                !CommandInvocationBuilder.TryGetPositiveDouble(parsed, "--detail", out double detailTargetM) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--timeout", out int timeoutSec) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--measure-performance", out bool measurePerformance) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--dry-run", out bool dryRun))
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>("Invalid command values.", RenderHelp);
            }

            if (latitude is < -90d or > 90d)
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>(
                    $"Invalid value for --latitude: {latitude.ToString(CultureInfo.InvariantCulture)}",
                    RenderHelp);
            }

            if (longitude is < -180d or > 180d)
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>(
                    $"Invalid value for --longitude: {longitude.ToString(CultureInfo.InvariantCulture)}",
                    RenderHelp);
            }

            if (string.IsNullOrWhiteSpace(resoniteHost))
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>("Invalid value for --resonite-host.", RenderHelp);
            }

            int resonitePort = 0;
            if (parsed.Values.TryGetValue("--resonite-port", out object? rawResonitePort) && rawResonitePort is not null)
            {
                if (!CommandInvocationBuilder.TryGetPort(parsed, "--resonite-port", out resonitePort))
                {
                    return CommandInvocationBuilder.Error<StreamCommandOptions>("Invalid value for --resonite-port.", RenderHelp);
                }
            }
            else if (!dryRun)
            {
                return CommandInvocationBuilder.Error<StreamCommandOptions>("Missing required argument: --resonite-port", RenderHelp);
            }

            return new CommandInvocation<StreamCommandOptions>(true, new StreamCommandOptions(
                latitude,
                longitude,
                heightOffset,
                rangeM,
                resoniteHost,
                resonitePort,
                detailTargetM,
                contentWorkers,
                resoniteSendWorkers,
                timeoutSec,
                measurePerformance,
                dryRun,
                logLevel), 0, string.Empty, false);
        }

        internal static string RenderHelp()
        {
            return CommandLineParser.RenderHelp(Specification);
        }
    }
}
