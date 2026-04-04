using ThreeDTilesLink.Core.Auth;
using DotNetEnv;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Mesh;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Resonite;
using ThreeDTilesLink.Core.Tiles;

int exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    try
    {
        _ = Env.TraversePath().NoClobber().Load();

        Dictionary<string, string?> parsed = ParseArgs(args);

        double lat = GetRequiredDouble(parsed, "--lat");
        double lon = GetRequiredDouble(parsed, "--lon");
        double heightM = GetOptionalDouble(parsed, "--height-offset-m", 0d);
        double halfWidthM = GetRequiredDouble(parsed, "--half-width-m");
        string linkHost = GetRequiredString(parsed, "--link-host");
        int linkPort = GetRequiredInt(parsed, "--link-port");

        int maxTiles = GetOptionalInt(parsed, "--max-tiles", 1024);
        int maxDepth = GetOptionalInt(parsed, "--max-depth", 32);
        double detailTargetM = GetOptionalDouble(parsed, "--detail-target-m", 30d);
        double renderStartSpanRatio = GetOptionalDouble(parsed, "--render-start-span-ratio", 4d);
        int timeoutSec = GetOptionalInt(parsed, "--timeout-sec", 120);
        bool dryRun = parsed.ContainsKey("--dry-run");
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

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSec)
        };

        var transformer = new GeographicCoordinateTransformer();
        var fetcher = new HttpTileContentFetcher(httpClient);
        var scheduler = new DefaultTileStreamingScheduler(
            new TileSelector(transformer),
            loggerFactory.CreateLogger<DefaultTileStreamingScheduler>());
        var extractor = new GlbMeshExtractor();
        var gcloudTokenProvider = new AdcAccessTokenProvider();
        var resonite = new ResoniteLinkClientAdapter();
        await using (resonite.ConfigureAwait(false))
        {
            var service = new TileStreamingService(
                fetcher,
                scheduler,
                extractor,
                transformer,
                resonite,
                gcloudTokenProvider,
                loggerFactory.CreateLogger<TileStreamingService>());

            var options = new StreamerOptions(
                new GeoReference(lat, lon, heightM),
                halfWidthM,
                linkHost,
                linkPort,
                maxTiles,
                maxDepth,
                detailTargetM,
                dryRun,
                apiKey,
                renderStartSpanRatio);

            RunSummary summary = await service.RunAsync(options, CancellationToken.None).ConfigureAwait(false);

            Console.WriteLine($"CandidateTiles={summary.CandidateTiles} ProcessedTiles={summary.ProcessedTiles} StreamedMeshes={summary.StreamedMeshes} FailedTiles={summary.FailedTiles}");
            return 0;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
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

static double GetRequiredDouble(IReadOnlyDictionary<string, string?> args, string key)
{
    string value = GetRequiredString(args, key);
    return !double.TryParse(value, out double parsed)
        ? throw new InvalidOperationException($"Invalid numeric value for {key}: {value}")
        : parsed;
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
