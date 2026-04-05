using Microsoft.Extensions.Logging;

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
        LogLevel LogLevel);

    internal static class InteractiveCommandLine
    {
        private static readonly CommandSpecification Specification = new(
            "dotnet run --project src/ThreeDTilesLink -- interactive [options]",
            "Attach watched variables in Resonite and keep streaming tiles as the watched latitude/longitude/range values change.",
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
                new("--debounce", CommandOptionValueKind.WholeNumber, "Delay after watched value changes before starting a run.", DefaultValue: 800, Unit: "ms", RenamedFrom: ["--debounce-ms"]),
                new("--throttle", CommandOptionValueKind.WholeNumber, "Minimum time between run starts.", DefaultValue: 3000, Unit: "ms", RenamedFrom: ["--throttle-ms"]),
                new("--remove-out-of-range", CommandOptionValueKind.Switch, "During overlapping updates, remove retained tiles that fall outside the latest range.", DefaultValue: false),
                new("--watch-path", CommandOptionValueKind.Text, "Watched variable path prefix.", DefaultValue: "World/ThreeDTilesLink", ValueName: "path", RenamedFrom: ["--watch-path-prefix"]),
                new("--watch-name", CommandOptionValueKind.Text, "Watch slot name.", DefaultValue: "3DTilesLink Watch", ValueName: "name"),
                CommonCommandOptions.DryRun(),
                CommonCommandOptions.LogLevelOption()
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--lat"] = "--lat is no longer supported in interactive mode.",
                ["--lon"] = "--lon is no longer supported in interactive mode.",
                ["--range"] = "--range is no longer supported in interactive mode. Set the watched Range value in Resonite instead.",
                ["--half-width-m"] = "--half-width-m is no longer supported in interactive mode. Set the watched Range value in Resonite instead.",
                ["--render-start-span-ratio"] = "--render-start-span-ratio is no longer supported."
            });

        internal static CommandInvocation<InteractiveCommandOptions> Parse(IReadOnlyList<string> args)
        {
            ParsedCommand parsed = CommandLineParser.Parse(Specification, args);
            if (parsed.Status == CommandParseStatus.Help)
            {
                return new CommandInvocation<InteractiveCommandOptions>(false, default, 0, parsed.Output, false);
            }

            if (parsed.Status == CommandParseStatus.Error)
            {
                return new CommandInvocation<InteractiveCommandOptions>(false, default, 1, parsed.Output, true);
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

            if (!TryGetValue(parsed, "--height-offset", out double heightOffsetM) ||
                !TryGetValue(parsed, "--resonite-host", out string? resoniteHost) ||
                !TryGetValue(parsed, "--resonite-port", out int resonitePort) ||
                !TryGetValue(parsed, "--tile-limit", out int tileLimit) ||
                !TryGetValue(parsed, "--depth-limit", out int depthLimit) ||
                !TryGetValue(parsed, "--detail", out double detailTargetM) ||
                !TryGetValue(parsed, "--timeout", out int timeoutSec) ||
                !TryGetValue(parsed, "--poll-interval", out int pollIntervalMs) ||
                !TryGetValue(parsed, "--debounce", out int debounceMs) ||
                !TryGetValue(parsed, "--throttle", out int throttleMs) ||
                !TryGetValue(parsed, "--remove-out-of-range", out bool removeOutOfRange) ||
                !TryGetValue(parsed, "--dry-run", out bool dryRun) ||
                !TryGetValue(parsed, "--watch-name", out string? watchName) ||
                !TryGetValue(parsed, "--watch-path", out string? watchPath))
            {
                return Error("Invalid command values.");
            }

            if (string.IsNullOrWhiteSpace(resoniteHost))
            {
                return Error("Invalid value for --resonite-host.");
            }

            if (string.IsNullOrWhiteSpace(watchName))
            {
                return Error("Invalid value for --watch-name.");
            }

            if (string.IsNullOrWhiteSpace(watchPath))
            {
                return Error("Invalid value for --watch-path.");
            }

            string normalizedWatchPath;
            try
            {
                normalizedWatchPath = NormalizeWatchPath(watchPath);
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }

            return new CommandInvocation<InteractiveCommandOptions>(true, new InteractiveCommandOptions(
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
                logLevel), 0, string.Empty, false);
        }

        internal static string RenderHelp()
        {
            return CommandLineParser.RenderHelp(Specification);
        }

        private static string NormalizeWatchPath(string input)
        {
            string trimmed = input.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new InvalidOperationException("Watch path cannot be empty.");
            }

            const string worldPrefix = "World/";
            if (!trimmed.StartsWith(worldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Watch path must start with 'World/'.");
            }

            string tail = trimmed[worldPrefix.Length..].Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(tail))
            {
                throw new InvalidOperationException("Watch path must contain a name after 'World/'.");
            }

            tail = tail.Replace('/', '.');
            string normalizedTail = NormalizePathSegments(tail);
            return $"{worldPrefix}{normalizedTail}";
        }

        private static string NormalizePathSegments(string tail)
        {
            string[] segments = tail.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                throw new InvalidOperationException("Watch path must contain at least one valid segment.");
            }

            var normalizedSegments = new List<string>(segments.Length);
            foreach (string segment in segments)
            {
                var chars = new List<char>(segment.Length);
                foreach (char ch in segment)
                {
                    if (char.IsLetterOrDigit(ch))
                    {
                        chars.Add(ch);
                    }
                }

                string cleaned = new string(chars.ToArray());
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    cleaned = "ThreeWatch";
                }

                if (!char.IsLetter(cleaned[0]))
                {
                    cleaned = $"Three{cleaned}";
                }

                normalizedSegments.Add(cleaned);
            }

            return string.Join('.', normalizedSegments);
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

        private static CommandInvocation<InteractiveCommandOptions> Error(string message)
        {
            return new CommandInvocation<InteractiveCommandOptions>(
                false,
                default,
                1,
                $"{message}{Environment.NewLine}{Environment.NewLine}{RenderHelp()}",
                true);
        }
    }
}
