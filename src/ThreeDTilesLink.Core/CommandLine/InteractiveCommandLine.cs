using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.App;

namespace ThreeDTilesLink.Core.CommandLine
{
    internal sealed record InteractiveCommandOptions(
        double HeightOffset,
        string EndpointHost,
        int EndpointPort,
        double DetailTargetM,
        int ContentWorkers,
        int ResoniteSendWorkers,
        int TimeoutSec,
        bool MeasurePerformance,
        int PollIntervalMs,
        int DebounceMs,
        int ThrottleMs,
        LogLevel LogLevel) : ICommandRuntimeOptions;

    internal static class InteractiveCommandLine
    {
        private static readonly CommandSpecification Specification = new(
            "dotnet run --project src/ThreeDTilesLink -- interactive [options]",
            "Attach the interactive UI adapter and keep streaming tiles as the selection Latitude/Longitude/Range/Search values change.",
            [
                CommonCommandOptions.HeightOffset(),
                CommonCommandOptions.EndpointHost(),
                CommonCommandOptions.EndpointPort(),
                CommonCommandOptions.DetailTarget(),
                CommonCommandOptions.ContentWorkers("Maximum number of tile content fetch/decode workers per run."),
                CommonCommandOptions.ResoniteSendWorkers("Maximum number of parallel Resonite send workers."),
                CommonCommandOptions.MeasurePerformance(),
                CommonCommandOptions.Timeout(),
                new("--poll-interval", CommandOptionValueKind.WholeNumber, "Interactive input polling interval.", DefaultValue: 250, Unit: "ms", RenamedFrom: ["--poll-ms"]),
                new("--debounce", CommandOptionValueKind.WholeNumber, "Delay after interactive input changes before starting a run.", DefaultValue: 800, Unit: "ms", RenamedFrom: ["--debounce-ms"]),
                new("--throttle", CommandOptionValueKind.WholeNumber, "Minimum time between run starts.", DefaultValue: 3000, Unit: "ms", RenamedFrom: ["--throttle-ms"]),
                CommonCommandOptions.LogLevelOption()
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--lat"] = "--lat is no longer supported in interactive mode.",
                ["--lon"] = "--lon is no longer supported in interactive mode.",
                ["--range"] = "--range is no longer supported in interactive mode. Set the interactive UI Range value instead.",
                ["--half-width-m"] = "--half-width-m is no longer supported in interactive mode. Set the interactive UI Range value instead.",
                ["--tile-limit"] = "--tile-limit is no longer supported in interactive mode.",
                ["--max-tiles"] = "--max-tiles is no longer supported in interactive mode.",
                ["--depth-limit"] = "--depth-limit is no longer supported in interactive mode.",
                ["--max-depth"] = "--max-depth is no longer supported in interactive mode.",
                ["--dry-run"] = "--dry-run is no longer supported in interactive mode.",
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
                !CommandInvocationBuilder.TryGetValue(parsed, "--endpoint-host", out string? endpointHost) ||
                !CommandInvocationBuilder.TryGetPort(parsed, "--endpoint-port", out int endpointPort) ||
                !CommandInvocationBuilder.TryGetPositiveDouble(parsed, "--detail", out double detailTargetM) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--timeout", out int timeoutSec) ||
                !CommandInvocationBuilder.TryGetValue(parsed, "--measure-performance", out bool measurePerformance) ||
                !CommandInvocationBuilder.TryGetPositiveInt(parsed, "--poll-interval", out int pollIntervalMs) ||
                !CommandInvocationBuilder.TryGetNonNegativeInt(parsed, "--debounce", out int debounceMs) ||
                !CommandInvocationBuilder.TryGetNonNegativeInt(parsed, "--throttle", out int throttleMs))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>("Invalid command values.", RenderHelp);
            }

            if (string.IsNullOrWhiteSpace(endpointHost))
            {
                return CommandInvocationBuilder.Error<InteractiveCommandOptions>("Invalid value for --endpoint-host.", RenderHelp);
            }

            return new CommandInvocation<InteractiveCommandOptions>(
                true,
                new InteractiveCommandOptions(
                    heightOffset,
                    endpointHost,
                    endpointPort,
                    detailTargetM,
                    contentWorkers,
                    resoniteSendWorkers,
                    timeoutSec,
                    measurePerformance,
                    pollIntervalMs,
                    debounceMs,
                    throttleMs,
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
