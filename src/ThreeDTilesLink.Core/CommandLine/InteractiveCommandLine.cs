using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.App;

namespace ThreeDTilesLink.Core.CommandLine
{
    internal sealed record InteractiveCommandOptions(
        double HeightOffset,
        string ResoniteHost,
        int ResonitePort,
        int TileLimit,
        int DepthLimit,
        double DetailTargetM,
        int ContentWorkers,
        int ResoniteSendWorkers,
        int TimeoutSec,
        bool MeasurePerformance,
        int PollIntervalMs,
        int DebounceMs,
        int ThrottleMs,
        bool DryRun,
        LogLevel LogLevel) : ICommandRuntimeOptions;

    internal static class InteractiveCommandLine
    {
        private static readonly CommandSpecification Specification = new(
            "dotnet run --project src/ThreeDTilesLink -- interactive [options]",
            "Attach interactive input variables in Resonite and keep streaming tiles as the session-root Latitude/Longitude/Range/Search values change.",
            [
                CommonCommandOptions.HeightOffset(),
                CommonCommandOptions.ResoniteHost(),
                CommonCommandOptions.ResonitePort(),
                CommonCommandOptions.TileLimit("Maximum number of tiles to stream per run."),
                CommonCommandOptions.DepthLimit("Maximum traversal depth per run."),
                CommonCommandOptions.DetailTarget(),
                CommonCommandOptions.ContentWorkers("Maximum number of tile content fetch/decode workers per run."),
                CommonCommandOptions.ResoniteSendWorkers("Maximum number of parallel Resonite send workers."),
                CommonCommandOptions.MeasurePerformance(),
                CommonCommandOptions.Timeout(),
                new("--poll-interval", CommandOptionValueKind.WholeNumber, "Interactive input polling interval.", DefaultValue: 250, Unit: "ms", RenamedFrom: ["--poll-ms"]),
                new("--debounce", CommandOptionValueKind.WholeNumber, "Delay after interactive input changes before starting a run.", DefaultValue: 800, Unit: "ms", RenamedFrom: ["--debounce-ms"]),
                new("--throttle", CommandOptionValueKind.WholeNumber, "Minimum time between run starts.", DefaultValue: 3000, Unit: "ms", RenamedFrom: ["--throttle-ms"]),
                CommonCommandOptions.DryRun(),
                CommonCommandOptions.LogLevelOption()
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--lat"] = "--lat is no longer supported in interactive mode.",
                ["--lon"] = "--lon is no longer supported in interactive mode.",
                ["--range"] = "--range is no longer supported in interactive mode. Set the Interactive Range value in Resonite instead.",
                ["--half-width-m"] = "--half-width-m is no longer supported in interactive mode. Set the Interactive Range value in Resonite instead.",
                ["--remove-out-of-range"] = "--remove-out-of-range is no longer supported. Interactive mode always removes retained tiles that fall outside the latest range.",
                ["--render-start-span-ratio"] = "--render-start-span-ratio is no longer supported."
            });

        internal static CommandInvocation<InteractiveCommandOptions> Parse(IReadOnlyList<string> args)
        {
            ParsedCommand parsed = CommandLineParser.Parse(Specification, args);
            if (CommandInvocationBuilder.TryHandleParseResult(parsed, out CommandInvocation<InteractiveCommandOptions> handled))
            {
                return handled;
            }

            if (!CommandInvocationBuilder.TryGetLogLevel(parsed, out LogLevel logLevel, out string? logLevelRaw))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>($"Invalid value for --log-level: {logLevelRaw}", RenderHelp);
            }

            if (!CommandInvocationBuilder.TryGetPositiveInt(parsed, "--content-workers", out int contentWorkers))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>($"Invalid value for --content-workers: {contentWorkers}", RenderHelp);
            }

            if (!CommandInvocationBuilder.TryGetPositiveInt(parsed, "--resonite-send-workers", out int resoniteSendWorkers))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>($"Invalid value for --resonite-send-workers: {resoniteSendWorkers}", RenderHelp);
            }

            if (!CommandInvocationBuilder.TryGetValue(parsed, "--height-offset", out double heightOffset) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--resonite-host", out string? resoniteHost) ||
                !CommandInvocationBuilder.TryGetPort(parsed, "--resonite-port", out int resonitePort) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--tile-limit", out int tileLimit) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--depth-limit", out int depthLimit) ||
                !CommandInvocationBuilder.TryGetPositiveDouble(parsed, "--detail", out double detailTargetM) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--timeout", out int timeoutSec) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--measure-performance", out bool measurePerformance) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--poll-interval", out int pollIntervalMs) ||
                !CommandInvocationBuilder.TryGetNonNegativeInt(parsed, "--debounce", out int debounceMs) ||
                !CommandInvocationBuilder.TryGetNonNegativeInt(parsed, "--throttle", out int throttleMs) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--dry-run", out bool dryRun))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>("Invalid command values.", RenderHelp);
            }

            if (string.IsNullOrWhiteSpace(resoniteHost))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>("Invalid value for --resonite-host.", RenderHelp);
            }

            return new CommandInvocation<InteractiveCommandOptions>(
                true,
                new InteractiveCommandOptions(
                    heightOffset,
                    resoniteHost,
                    resonitePort,
                    tileLimit,
                    depthLimit,
                    detailTargetM,
                    contentWorkers,
                    resoniteSendWorkers,
                    timeoutSec,
                    measurePerformance,
                    pollIntervalMs,
                    debounceMs,
                    throttleMs,
                    dryRun,
                    logLevel),
                0,
                string.Empty,
                false);
        }

        internal static string RenderHelp()
        {
            return CommandLineParser.RenderHelp(Specification);
        }
    }
}
