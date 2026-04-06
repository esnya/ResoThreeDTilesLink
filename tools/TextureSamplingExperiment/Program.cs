// Experimental console tool; localized resources are unnecessary here.
#pragma warning disable CA1303
using System.Diagnostics.CodeAnalysis;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Mesh;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

return await RunAsync(args).ConfigureAwait(false);

[SuppressMessage(
    "Reliability",
    "CA1031:DoNotCatchGeneralExceptionTypes",
    Justification = "The sampler converts unexpected failures into a user-visible non-zero exit code.")]
static async Task<int> RunAsync(string[] args)
{
    try
    {
        SamplingOptions options = SamplingOptions.Parse(args);
        string apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("GOOGLE_MAPS_API_KEY is required.");
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSec)
        };
        var tilesSource = new HttpTilesSource(httpClient);
        var extractor = new GlbMeshExtractor();
        var selector = new TileSelector(new GeographicCoordinateTransformer());
        var auth = new GoogleTilesAuth(apiKey, null);

        Tileset root = await tilesSource.FetchRootTilesetAsync(auth, CancellationToken.None).ConfigureAwait(false);
        IReadOnlyList<TileSelectionResult> selected = selector.Select(
            root,
            new GeoReference(options.Latitude, options.Longitude, options.HeightOffsetM),
            new QueryRange(options.RangeM),
            options.DepthLimit,
            options.DetailTargetM,
            options.TileLimit,
            Matrix4x4d.Identity,
            string.Empty,
            0,
            null,
            null);

        TileSelectionResult[] glbTiles = selected
            .Where(static tile => tile.ContentKind == TileContentKind.Glb)
            .Take(options.SampleCount)
            .ToArray();

        if (glbTiles.Length == 0)
        {
            throw new InvalidOperationException("No GLB tiles were selected for sampling.");
        }

        var sampleResults = new List<TextureSampleResult>(glbTiles.Length);
        foreach (TileSelectionResult tile in glbTiles)
        {
            FetchedNodeContent content = await tilesSource.FetchNodeContentAsync(tile.ContentUri, auth, CancellationToken.None).ConfigureAwait(false);
            if (content is not GlbFetchedContent glb)
            {
                continue;
            }

            GlbExtractResult extracted = extractor.Extract(glb.GlbBytes);
            sampleResults.Add(TextureSampleResult.From(tile, extracted));
        }

        PrintSummary(options, selected.Count, glbTiles.Length, sampleResults);
        return 0;
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
        return 1;
    }
}

static void PrintSummary(
    SamplingOptions options,
    int selectedTileCount,
    int sampledTileCount,
    IReadOnlyList<TextureSampleResult> results)
{
    int[] allTextureSizes = results
        .SelectMany(static result => result.TextureSizes)
        .OrderBy(static size => size)
        .ToArray();

    Console.WriteLine($"Area lat={options.Latitude} lon={options.Longitude} range={options.RangeM}m");
    Console.WriteLine($"SelectedGlbTiles={selectedTileCount} SampledGlbTiles={sampledTileCount} SampledMeshes={results.Sum(static result => result.MeshCount)}");
    Console.WriteLine($"TilesWithTextures={results.Count(static result => result.TexturedMeshCount > 0)} TilesWithoutTextures={results.Count(static result => result.TexturedMeshCount == 0)}");

    if (allTextureSizes.Length == 0)
    {
        Console.WriteLine("No textures found in sampled meshes.");
        return;
    }

    Console.WriteLine($"TextureCount={allTextureSizes.Length}");
    Console.WriteLine($"TextureBytes min={FormatBytes(allTextureSizes[0])} p50={FormatBytes(Percentile(allTextureSizes, 0.50))} p90={FormatBytes(Percentile(allTextureSizes, 0.90))} p95={FormatBytes(Percentile(allTextureSizes, 0.95))} max={FormatBytes(allTextureSizes[^1])}");
    Console.WriteLine();
    Console.WriteLine("PerTile:");

    foreach (TextureSampleResult result in results)
    {
        Console.WriteLine(
            $"{result.TileId} meshes={result.MeshCount} textured={result.TexturedMeshCount} " +
            $"min={FormatBytes(result.MinTextureBytes)} p50={FormatBytes(result.P50TextureBytes)} max={FormatBytes(result.MaxTextureBytes)} uri={result.ContentUri}");
    }
}

static int Percentile(int[] sortedValues, double percentile)
{
    if (sortedValues.Length == 0)
    {
        return 0;
    }

    int index = (int)Math.Ceiling((sortedValues.Length * percentile) - 1d);
    index = Math.Clamp(index, 0, sortedValues.Length - 1);
    return sortedValues[index];
}

static string FormatBytes(int bytes)
{
    if (bytes <= 0)
    {
        return "0 B";
    }

    string[] units = ["B", "KB", "MB", "GB"];
    double value = bytes;
    int unitIndex = 0;
    while (value >= 1024d && unitIndex < units.Length - 1)
    {
        value /= 1024d;
        unitIndex++;
    }

    return $"{value:F2} {units[unitIndex]}";
}

internal sealed record SamplingOptions(
    double Latitude,
    double Longitude,
    double HeightOffsetM,
    double RangeM,
    int TileLimit,
    int DepthLimit,
    double DetailTargetM,
    int SampleCount,
    int TimeoutSec)
{
    public static SamplingOptions Parse(IReadOnlyList<string> args)
    {
        double latitude = 35.65858;
        double longitude = 139.745433;
        double heightOffsetM = 0d;
        double rangeM = 60d;
        int tileLimit = 64;
        int depthLimit = 12;
        double detailTargetM = 12d;
        int sampleCount = 16;
        int timeoutSec = 60;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--latitude":
                    latitude = double.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--longitude":
                    longitude = double.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--height-offset":
                    heightOffsetM = double.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--range":
                    rangeM = double.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--tile-limit":
                    tileLimit = int.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--depth-limit":
                    depthLimit = int.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--detail":
                    detailTargetM = double.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--sample-count":
                    sampleCount = int.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--timeout-sec":
                    timeoutSec = int.Parse(ReadRequiredValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--help":
                case "-h":
                    PrintHelpAndExit(0);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument: {arg}");
            }
        }

        return new SamplingOptions(latitude, longitude, heightOffsetM, rangeM, tileLimit, depthLimit, detailTargetM, sampleCount, timeoutSec);
    }

    private static string ReadRequiredValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new InvalidOperationException($"Missing value for {optionName}");
        }

        index++;
        return args[index];
    }

    private static void PrintHelpAndExit(int exitCode)
    {
        Console.WriteLine("Usage: dotnet run --project tools/TextureSamplingExperiment -- [options]");
        Console.WriteLine("  --latitude <deg>          Default: 35.65858");
        Console.WriteLine("  --longitude <deg>         Default: 139.745433");
        Console.WriteLine("  --height-offset <m>       Default: 0");
        Console.WriteLine("  --range <m>               Default: 60");
        Console.WriteLine("  --tile-limit <count>      Default: 64");
        Console.WriteLine("  --depth-limit <count>     Default: 12");
        Console.WriteLine("  --detail <m>              Default: 12");
        Console.WriteLine("  --sample-count <count>    Default: 16");
        Console.WriteLine("  --timeout-sec <sec>       Default: 60");
        Environment.Exit(exitCode);
    }
}

internal sealed record TextureSampleResult(
    string TileId,
    Uri ContentUri,
    int MeshCount,
    int TexturedMeshCount,
    int[] TextureSizes,
    int MinTextureBytes,
    int P50TextureBytes,
    int MaxTextureBytes)
{
    public static TextureSampleResult From(TileSelectionResult tile, GlbExtractResult extracted)
    {
        int[] textureSizes = extracted.Meshes
            .Select(static mesh => mesh.BaseColorTextureBytes?.Length ?? 0)
            .Where(static size => size > 0)
            .OrderBy(static size => size)
            .ToArray();

        int min = textureSizes.Length == 0 ? 0 : textureSizes[0];
        int p50 = textureSizes.Length == 0 ? 0 : textureSizes[(textureSizes.Length - 1) / 2];
        int max = textureSizes.Length == 0 ? 0 : textureSizes[^1];

        return new TextureSampleResult(
            tile.TileId,
            tile.ContentUri,
            extracted.Meshes.Count,
            textureSizes.Length,
            textureSizes,
            min,
            p50,
            max);
    }
}
