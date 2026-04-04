using DotNetEnv;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Runtime;

int exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    try
    {
        _ = Env.TraversePath().NoClobber().Load();

        CommandInvocation<CliCommandOptions> invocation = CliCommandLine.Parse(args);
        if (!invocation.ShouldRun)
        {
            WriteOutput(invocation.Output, invocation.WriteToError);
            return invocation.ExitCode;
        }

        CliCommandOptions parsed = invocation.Options!;

        string apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? string.Empty;

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder
                .SetMinimumLevel(parsed.LogLevel)
                .AddSimpleConsole(options =>
                {
                    options.IncludeScopes = false;
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                });
        });

        var runtime = new TileStreamingRuntime(loggerFactory, TimeSpan.FromSeconds(parsed.TimeoutSec));
        await using (runtime.ConfigureAwait(false))
        {
            var options = new StreamerOptions(
                new GeoReference(parsed.Latitude, parsed.Longitude, parsed.HeightOffsetM),
                parsed.RangeM,
                parsed.ResoniteHost,
                parsed.ResonitePort,
                parsed.TileLimit,
                parsed.DepthLimit,
                parsed.DetailTargetM,
                parsed.DryRun,
                apiKey);

            RunSummary summary = await runtime.RunAsync(options, CancellationToken.None).ConfigureAwait(false);

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

static void WriteOutput(string output, bool writeToError)
{
    if (string.IsNullOrWhiteSpace(output))
    {
        return;
    }

    if (writeToError)
    {
        Console.Error.WriteLine(output);
        return;
    }

    Console.WriteLine(output);
}
