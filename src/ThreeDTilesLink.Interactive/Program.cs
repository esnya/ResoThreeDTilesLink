using DotNetEnv;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Runtime;

int exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    ConsoleCancelEventHandler? cancelHandler = null;

    try
    {
        _ = Env.TraversePath().NoClobber().Load();

        Dictionary<string, string?> parsed = ParseArgs(args);

        double heightM = GetOptionalDouble(parsed, "--height-offset-m", 0d);
        string linkHost = GetRequiredString(parsed, "--link-host");
        int linkPort = GetRequiredInt(parsed, "--link-port");

        int maxTiles = GetOptionalInt(parsed, "--max-tiles", 1024);
        int maxDepth = GetOptionalInt(parsed, "--max-depth", 32);
        double detailTargetM = GetOptionalDouble(parsed, "--detail-target-m", 30d);
        double renderStartSpanRatio = GetOptionalDouble(parsed, "--render-start-span-ratio", 4d);
        int timeoutSec = GetOptionalInt(parsed, "--timeout-sec", 120);
        int pollMs = GetOptionalInt(parsed, "--poll-ms", 250);
        int debounceMs = GetOptionalInt(parsed, "--debounce-ms", 800);
        int throttleMs = GetOptionalInt(parsed, "--throttle-ms", 3000);
        bool dryRun = parsed.ContainsKey("--dry-run");
        string probeSlotName = GetOptionalString(parsed, "--probe-slot-name", "3DTilesLink Probe");
        string probePrefix = NormalizePathPrefix(GetOptionalString(parsed, "--probe-path-prefix", "World/3DTilesLink"));
        LogLevel logLevel = ParseLogLevel(GetOptionalString(parsed, "--log-level", "Information"));

        string apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? string.Empty;

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder
                .SetMinimumLevel(logLevel)
                .AddSimpleConsole(options =>
                {
                    options.IncludeScopes = false;
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                });
        });

        using var appCts = new CancellationTokenSource();
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            appCts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var runtime = new TileStreamingRuntime(loggerFactory, TimeSpan.FromSeconds(timeoutSec));
        await using (runtime.ConfigureAwait(false))
        {
            var probeConfiguration = new ProbeConfiguration(
                probeSlotName,
                $"{probePrefix}.Latitude",
                $"{probePrefix}.Longitude",
                $"{probePrefix}.Range");

            var options = new ProbeDrivenStreamerOptions(
                linkHost,
                linkPort,
                heightM,
                maxTiles,
                maxDepth,
                detailTargetM,
                dryRun,
                apiKey,
                renderStartSpanRatio,
                TimeSpan.FromMilliseconds(pollMs),
                TimeSpan.FromMilliseconds(debounceMs),
                TimeSpan.FromMilliseconds(throttleMs),
                probeConfiguration);

            var service = new ProbeDrivenStreamingService(
                runtime.StreamingService,
                runtime.ResoniteLinkClient,
                loggerFactory.CreateLogger<ProbeDrivenStreamingService>());

            Console.WriteLine(
                $"Interactive mode started. Probe={probePrefix} (lat/lon/range). Poll={pollMs}ms Debounce={debounceMs}ms Throttle={throttleMs}ms. Press Ctrl+C to stop.");
            await service.RunAsync(options, appCts.Token).ConfigureAwait(false);
            return 0;
        }
    }
    catch (OperationCanceledException)
    {
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    finally
    {
        if (cancelHandler is not null)
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}

static string NormalizePathPrefix(string input)
{
    string trimmed = input.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        throw new InvalidOperationException("Probe path prefix cannot be empty.");
    }

    const string worldPrefix = "World/";
    if (!trimmed.StartsWith(worldPrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Probe path prefix must start with 'World/'.");
    }

    string tail = trimmed[worldPrefix.Length..].Trim().Trim('/');
    if (string.IsNullOrWhiteSpace(tail))
    {
        throw new InvalidOperationException("Probe path prefix must contain a name after 'World/'.");
    }

    // Resonite dynamic variable path does not accept nested '/' after World/.
    tail = tail.Replace('/', '.');
    string normalizedTail = NormalizePathSegments(tail);
    return $"{worldPrefix}{normalizedTail}";
}

static string NormalizePathSegments(string tail)
{
    string[] segments = tail.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (segments.Length == 0)
    {
        throw new InvalidOperationException("Probe path prefix must contain at least one valid segment.");
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

static Dictionary<string, string?> ParseArgs(IReadOnlyList<string> args)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    for (int i = 0; i < args.Count; i++)
    {
        string current = args[i];
        if (!current.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        int equalsIndex = current.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex > 2)
        {
            string key = current[..equalsIndex];
            string value = current[(equalsIndex + 1)..];
            result[key] = string.IsNullOrWhiteSpace(value) ? null : value;
            continue;
        }

        if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            result[current] = args[i + 1];
            i++;
        }
        else
        {
            result[current] = null;
        }
    }

    return result;
}

static string GetRequiredString(IReadOnlyDictionary<string, string?> args, string key)
{
    return !args.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"Missing required argument: {key}")
        : value;
}

static int GetRequiredInt(IReadOnlyDictionary<string, string?> args, string key)
{
    string value = GetRequiredString(args, key);
    return !int.TryParse(value, out int parsed)
        ? throw new InvalidOperationException($"Invalid integer value for {key}: {value}")
        : parsed;
}

static int GetOptionalInt(IReadOnlyDictionary<string, string?> args, string key, int fallback)
{
    return !args.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)
        ? fallback
        : int.TryParse(value, out int parsed)
        ? parsed
        : throw new InvalidOperationException($"Invalid integer value for {key}: {value}");
}

static double GetOptionalDouble(IReadOnlyDictionary<string, string?> args, string key, double fallback)
{
    return !args.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)
        ? fallback
        : double.TryParse(value, out double parsed)
        ? parsed
        : throw new InvalidOperationException($"Invalid numeric value for {key}: {value}");
}

static string GetOptionalString(IReadOnlyDictionary<string, string?> args, string key, string fallback)
{
    return !args.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) ? fallback : value;
}

static LogLevel ParseLogLevel(string value)
{
    return Enum.TryParse<LogLevel>(value, ignoreCase: true, out LogLevel parsed)
        ? parsed
        : LogLevel.Information;
}
