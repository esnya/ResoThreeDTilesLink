using DotNetEnv;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Runtime;

int exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    ConsoleCancelEventHandler? cancelHandler = null;

    try
    {
        _ = Env.TraversePath().NoClobber().Load();

        CommandInvocation<InteractiveCommandOptions> invocation = InteractiveCommandLine.Parse(args);
        if (!invocation.ShouldRun)
        {
            WriteOutput(invocation.Output, invocation.WriteToError);
            return invocation.ExitCode;
        }

        InteractiveCommandOptions parsed = invocation.Options!;
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

        using var appCts = new CancellationTokenSource();
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            appCts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var runtime = new TileStreamingRuntime(loggerFactory, TimeSpan.FromSeconds(parsed.TimeoutSec));
        await using (runtime.ConfigureAwait(false))
        {
            var request = new InteractiveRunRequest(
                parsed.ResoniteHost,
                parsed.ResonitePort,
                parsed.HeightOffsetM,
                new TraversalOptions(
                    RangeM: 0d,
                    parsed.TileLimit,
                    parsed.DepthLimit,
                    parsed.DetailTargetM),
                parsed.DryRun,
                apiKey,
                new ProbeWatchOptions(
                    TimeSpan.FromMilliseconds(parsed.PollIntervalMs),
                    TimeSpan.FromMilliseconds(parsed.DebounceMs),
                    TimeSpan.FromMilliseconds(parsed.ThrottleMs),
                    new ProbeConfiguration(
                        parsed.ProbeName,
                        $"{parsed.ProbePath}.Latitude",
                        $"{parsed.ProbePath}.Longitude",
                        $"{parsed.ProbePath}.Range",
                        $"{parsed.ProbePath}.Search")));

            Console.WriteLine(
                $"Interactive mode started. Probe={parsed.ProbePath} (lat/lon/range/search). Poll={parsed.PollIntervalMs}ms Debounce={parsed.DebounceMs}ms Throttle={parsed.ThrottleMs}ms. Press Ctrl+C to stop.");
            await runtime.InteractiveSupervisor.RunAsync(request, appCts.Token).ConfigureAwait(false);
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
