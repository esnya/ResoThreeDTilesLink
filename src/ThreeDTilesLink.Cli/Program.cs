using ThreeDTilesLink.Core.Auth;
using DotNetEnv;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Mesh;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Resonite;
using ThreeDTilesLink.Core.Tiles;

var exitCode = await RunAsync(args);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    try
    {
        Env.TraversePath().NoClobber().Load();

        var parsed = ParseArgs(args);

        var lat = GetRequiredDouble(parsed, "--lat");
        var lon = GetRequiredDouble(parsed, "--lon");
        var heightM = GetOptionalDouble(parsed, "--height-offset-m", 0d);
        var halfWidthM = GetRequiredDouble(parsed, "--half-width-m");
        var linkHost = GetRequiredString(parsed, "--link-host");
        var linkPort = GetRequiredInt(parsed, "--link-port");

        var maxTiles = GetOptionalInt(parsed, "--max-tiles", 1024);
        var maxDepth = GetOptionalInt(parsed, "--max-depth", 32);
        var detailTargetM = GetOptionalDouble(parsed, "--detail-target-m", 30d);
        var timeoutSec = GetOptionalInt(parsed, "--timeout-sec", 120);
        var dryRun = parsed.ContainsKey("--dry-run");
        var logLevel = ParseLogLevel(GetOptionalString(parsed, "--log-level", "Information"));

        var apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? string.Empty;

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
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
        var parser = new TilesetParser();
        var fetcher = new HttpTileContentFetcher(httpClient, parser);
        var selector = new TileSelector(transformer);
        var extractor = new GlbMeshExtractor();
        var gcloudTokenProvider = new AdcAccessTokenProvider();
        await using var resonite = new ResoniteLinkClientAdapter();

        var service = new TileStreamingService(
            fetcher,
            selector,
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
            apiKey);

        var summary = await service.RunAsync(options, CancellationToken.None);

        Console.WriteLine($"CandidateTiles={summary.CandidateTiles} ProcessedTiles={summary.ProcessedTiles} StreamedMeshes={summary.StreamedMeshes} FailedTiles={summary.FailedTiles}");
        return 0;
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

    for (var i = 0; i < args.Count; i++)
    {
        var current = args[i];
        if (!current.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var equalsIndex = current.IndexOf('=');
        if (equalsIndex > 2)
        {
            var key = current[..equalsIndex];
            var value = current[(equalsIndex + 1)..];
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
    if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Missing required argument: {key}");
    }

    return value;
}

static double GetRequiredDouble(IReadOnlyDictionary<string, string?> args, string key)
{
    var value = GetRequiredString(args, key);
    if (!double.TryParse(value, out var parsed))
    {
        throw new InvalidOperationException($"Invalid numeric value for {key}: {value}");
    }

    return parsed;
}

static int GetRequiredInt(IReadOnlyDictionary<string, string?> args, string key)
{
    var value = GetRequiredString(args, key);
    if (!int.TryParse(value, out var parsed))
    {
        throw new InvalidOperationException($"Invalid integer value for {key}: {value}");
    }

    return parsed;
}

static int GetOptionalInt(IReadOnlyDictionary<string, string?> args, string key, int fallback)
{
    if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
    {
        return fallback;
    }

    if (int.TryParse(value, out var parsed))
    {
        return parsed;
    }

    throw new InvalidOperationException($"Invalid integer value for {key}: {value}");
}

static double GetOptionalDouble(IReadOnlyDictionary<string, string?> args, string key, double fallback)
{
    if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
    {
        return fallback;
    }

    if (double.TryParse(value, out var parsed))
    {
        return parsed;
    }

    throw new InvalidOperationException($"Invalid numeric value for {key}: {value}");
}

static string GetOptionalString(IReadOnlyDictionary<string, string?> args, string key, string fallback)
{
    if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
    {
        return fallback;
    }

    return value;
}

static LogLevel ParseLogLevel(string value)
{
    return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed)
        ? parsed
        : LogLevel.Information;
}
