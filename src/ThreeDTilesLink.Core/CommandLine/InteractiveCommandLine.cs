using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.App;

namespace ThreeDTilesLink.Core.CommandLine
{
    internal sealed record InteractiveCommandOptions(
        double HeightOffsetM,
        string ResoniteHost,
        int ResonitePort,
        int TileLimit,
        int DepthLimit,
        double DetailTargetM,
        int ContentWorkers,
        int TimeoutSec,
        int PollIntervalMs,
        int DebounceMs,
        int ThrottleMs,
        bool RemoveOutOfRange,
        bool DryRun,
        string WatchName,
        string WatchPath,
        LogLevel LogLevel) : ICommandRuntimeOptions;

    internal static class InteractiveCommandLine
    {
        private static readonly CommandSpecification Specification = new(
            "dotnet run --project src/ThreeDTilesLink -- interactive [options]",
            "Attach watch variables in Resonite and keep streaming tiles as the watch latitude/longitude/range values change.",
            [
                CommonCommandOptions.HeightOffset(),
                CommonCommandOptions.ResoniteHost(),
                CommonCommandOptions.ResonitePort(),
                CommonCommandOptions.TileLimit("Maximum number of tiles to stream per run."),
                CommonCommandOptions.DepthLimit("Maximum traversal depth per run."),
                CommonCommandOptions.DetailTarget(),
                CommonCommandOptions.ContentWorkers("Maximum number of tile content fetch/decode workers per run."),
                CommonCommandOptions.Timeout(),
                new("--poll-interval", CommandOptionValueKind.WholeNumber, "Watch polling interval.", DefaultValue: 250, Unit: "ms", RenamedFrom: ["--poll-ms"]),
                new("--debounce", CommandOptionValueKind.WholeNumber, "Delay after watch changes before starting a run.", DefaultValue: 800, Unit: "ms", RenamedFrom: ["--debounce-ms"]),
                new("--throttle", CommandOptionValueKind.WholeNumber, "Minimum time between run starts.", DefaultValue: 3000, Unit: "ms", RenamedFrom: ["--throttle-ms"]),
                new("--remove-out-of-range", CommandOptionValueKind.Switch, "During overlapping updates, remove retained tiles that fall outside the latest range.", DefaultValue: false),
                new("--watch-path", CommandOptionValueKind.Text, "Watch variable path prefix.", DefaultValue: "World/ThreeDTilesLink", ValueName: "path"),
                new("--watch-name", CommandOptionValueKind.Text, "Watch slot name.", DefaultValue: "3DTilesLink Watch", ValueName: "name"),
                CommonCommandOptions.DryRun(),
                CommonCommandOptions.LogLevelOption()
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--lat"] = "--lat is no longer supported in interactive mode.",
                ["--lon"] = "--lon is no longer supported in interactive mode.",
                ["--range"] = "--range is no longer supported in interactive mode. Set the watch Range value in Resonite instead.",
                ["--half-width-m"] = "--half-width-m is no longer supported in interactive mode. Set the watch Range value in Resonite instead.",
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

            if (!CommandInvocationBuilder.TryGetValue(parsed, "--height-offset", out double heightOffsetM) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--resonite-host", out string? resoniteHost) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--resonite-port", out int resonitePort) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--tile-limit", out int tileLimit) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--depth-limit", out int depthLimit) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--detail", out double detailTargetM) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--timeout", out int timeoutSec) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--poll-interval", out int pollIntervalMs) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--debounce", out int debounceMs) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--throttle", out int throttleMs) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--remove-out-of-range", out bool removeOutOfRange) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--dry-run", out bool dryRun) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--watch-name", out string? watchName) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--watch-path", out string? watchPath))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>("Invalid command values.", RenderHelp);
            }

            if (string.IsNullOrWhiteSpace(resoniteHost))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>("Invalid value for --resonite-host.", RenderHelp);
            }

            if (string.IsNullOrWhiteSpace(watchName))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>("Invalid value for --watch-name.", RenderHelp);
            }

            if (string.IsNullOrWhiteSpace(watchPath))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>("Invalid value for --watch-path.", RenderHelp);
            }

            string normalizedWatchPath;
            try
            {
                normalizedWatchPath = InteractiveWatchPath.Parse(watchPath).BasePath;
            }
            catch (InvalidOperationException ex)
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>(ex.Message, RenderHelp);
            }

            return new CommandInvocation<InteractiveCommandOptions>(
                true,
                new InteractiveCommandOptions(
                    heightOffsetM,
                    resoniteHost,
                    resonitePort,
                    tileLimit,
                    depthLimit,
                    detailTargetM,
                    contentWorkers,
                    timeoutSec,
                    pollIntervalMs,
                    debounceMs,
                    throttleMs,
                    removeOutOfRange,
                    dryRun,
                    watchName,
                    normalizedWatchPath,
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
