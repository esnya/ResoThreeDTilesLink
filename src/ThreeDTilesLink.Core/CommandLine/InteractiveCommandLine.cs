using Microsoft.Extensions.Logging;

namespace ThreeDTilesLink.Core.CommandLine
{
    public sealed record InteractiveCommandOptions(
        double HeightOffsetM,
        string ResoniteHost,
        int ResonitePort,
        int TileLimit,
        int DepthLimit,
        double DetailTargetM,
        int TimeoutSec,
        int PollIntervalMs,
        int DebounceMs,
        int ThrottleMs,
        bool RemoveOutOfRange,
        bool DryRun,
        string ProbeName,
        string ProbePath,
        LogLevel LogLevel);

    public static class InteractiveCommandLine
    {
        private static readonly CommandSpecification Specification = new(
            "dotnet run --project src/ThreeDTilesLink.Interactive -- [options]",
            "Attach probe variables in Resonite and keep streaming tiles as the probe latitude/longitude/range values change.",
            [
                new("--height-offset", CommandOptionValueKind.DecimalNumber, "Height offset applied to streamed geometry.", DefaultValue: 0d, Unit: "m", RenamedFrom: ["--height-offset-m"]),
                new("--resonite-host", CommandOptionValueKind.Text, "Resonite Link host name or IP address.", Required: true, ValueName: "host", RenamedFrom: ["--link-host"]),
                new("--resonite-port", CommandOptionValueKind.WholeNumber, "Resonite Link port.", Required: true, ValueName: "port", RenamedFrom: ["--link-port"]),
                new("--tile-limit", CommandOptionValueKind.WholeNumber, "Maximum number of tiles to stream per run.", DefaultValue: 1024, RenamedFrom: ["--max-tiles"]),
                new("--depth-limit", CommandOptionValueKind.WholeNumber, "Maximum traversal depth per run.", DefaultValue: 32, RenamedFrom: ["--max-depth"]),
                new("--detail", CommandOptionValueKind.DecimalNumber, "Target tile detail before traversal stops descending renderable GLB tiles.", DefaultValue: 30d, Unit: "m", RenamedFrom: ["--detail-target-m"]),
                new("--timeout", CommandOptionValueKind.WholeNumber, "Request timeout.", DefaultValue: 120, Unit: "sec", RenamedFrom: ["--timeout-sec"]),
                new("--poll-interval", CommandOptionValueKind.WholeNumber, "Probe polling interval.", DefaultValue: 250, Unit: "ms", RenamedFrom: ["--poll-ms"]),
                new("--debounce", CommandOptionValueKind.WholeNumber, "Delay after probe changes before starting a run.", DefaultValue: 800, Unit: "ms", RenamedFrom: ["--debounce-ms"]),
                new("--throttle", CommandOptionValueKind.WholeNumber, "Minimum time between run starts.", DefaultValue: 3000, Unit: "ms", RenamedFrom: ["--throttle-ms"]),
                new("--remove-out-of-range", CommandOptionValueKind.Switch, "During overlapping updates, remove retained tiles that fall outside the latest range.", DefaultValue: false),
                new("--probe-path", CommandOptionValueKind.Text, "Probe variable path prefix.", DefaultValue: "World/ThreeDTilesLink", ValueName: "path", RenamedFrom: ["--probe-path-prefix"]),
                new("--probe-name", CommandOptionValueKind.Text, "Probe slot name.", DefaultValue: "3DTilesLink Probe", ValueName: "name", RenamedFrom: ["--probe-slot-name"]),
                new("--dry-run", CommandOptionValueKind.Switch, "Fetch and convert tiles without sending anything to Resonite.", DefaultValue: false),
                new("--log-level", CommandOptionValueKind.Text, "Logging level.", DefaultValue: "Information", ValueName: "level")
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--lat"] = "--lat is no longer supported in interactive mode.",
                ["--lon"] = "--lon is no longer supported in interactive mode.",
                ["--range"] = "--range is no longer supported in interactive mode. Set the probe Range value in Resonite instead.",
                ["--half-width-m"] = "--half-width-m is no longer supported in interactive mode. Set the probe Range value in Resonite instead.",
                ["--render-start-span-ratio"] = "--render-start-span-ratio is no longer supported."
            });

        public static CommandInvocation<InteractiveCommandOptions> Parse(IReadOnlyList<string> args)
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

            try
            {
                string logLevelRaw = (string)parsed.Values["--log-level"]!;
                if (!Enum.TryParse(logLevelRaw, ignoreCase: true, out LogLevel logLevel))
                {
                    return Error($"Invalid value for --log-level: {logLevelRaw}");
                }

                string probePath = NormalizeProbePath((string)parsed.Values["--probe-path"]!);
                return new CommandInvocation<InteractiveCommandOptions>(true, new InteractiveCommandOptions(
                    (double)parsed.Values["--height-offset"]!,
                    (string)parsed.Values["--resonite-host"]!,
                    (int)parsed.Values["--resonite-port"]!,
                    (int)parsed.Values["--tile-limit"]!,
                    (int)parsed.Values["--depth-limit"]!,
                    (double)parsed.Values["--detail"]!,
                    (int)parsed.Values["--timeout"]!,
                    (int)parsed.Values["--poll-interval"]!,
                    (int)parsed.Values["--debounce"]!,
                    (int)parsed.Values["--throttle"]!,
                    (bool)parsed.Values["--remove-out-of-range"]!,
                    (bool)parsed.Values["--dry-run"]!,
                    (string)parsed.Values["--probe-name"]!,
                    probePath,
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

        private static string NormalizeProbePath(string input)
        {
            string trimmed = input.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new InvalidOperationException("Probe path cannot be empty.");
            }

            const string worldPrefix = "World/";
            if (!trimmed.StartsWith(worldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Probe path must start with 'World/'.");
            }

            string tail = trimmed[worldPrefix.Length..].Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(tail))
            {
                throw new InvalidOperationException("Probe path must contain a name after 'World/'.");
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
                throw new InvalidOperationException("Probe path must contain at least one valid segment.");
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
                    cleaned = "ThreeProbe";
                }

                if (!char.IsLetter(cleaned[0]))
                {
                    cleaned = $"Three{cleaned}";
                }

                normalizedSegments.Add(cleaned);
            }

            return string.Join('.', normalizedSegments);
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
