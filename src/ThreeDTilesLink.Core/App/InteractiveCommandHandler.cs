using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Runtime;

namespace ThreeDTilesLink.Core.App
{
    public static class InteractiveCommandHandler
    {
        public static InteractiveRunRequest CreateRequest(InteractiveCommandOptions options, string apiKey)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new InteractiveRunRequest(
                options.ResoniteHost,
                options.ResonitePort,
                options.HeightOffsetM,
                new TraversalOptions(
                    RangeM: 0d,
                    options.TileLimit,
                    options.DepthLimit,
                    options.DetailTargetM),
                options.DryRun,
                apiKey,
                options.RemoveOutOfRange,
                new ProbeWatchOptions(
                    TimeSpan.FromMilliseconds(options.PollIntervalMs),
                    TimeSpan.FromMilliseconds(options.DebounceMs),
                    TimeSpan.FromMilliseconds(options.ThrottleMs),
                    new ProbeConfiguration(
                        options.ProbeName,
                        $"{options.ProbePath}.Latitude",
                        $"{options.ProbePath}.Longitude",
                        $"{options.ProbePath}.Range",
                        $"{options.ProbePath}.Search")));
        }

        public static async Task<int> RunAsync(
            InteractiveCommandOptions options,
            TileStreamingRuntime runtime,
            string apiKey,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(output);

            ConsoleCancelEventHandler? cancelHandler = null;
            using var appCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                cancelHandler = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    appCts.Cancel();
                };
                Console.CancelKeyPress += cancelHandler;

                await output.WriteLineAsync(
                    $"Interactive mode started. Probe={options.ProbePath} (lat/lon/range/search). Poll={options.PollIntervalMs}ms Debounce={options.DebounceMs}ms Throttle={options.ThrottleMs}ms. Press Ctrl+C to stop.")
                    .ConfigureAwait(false);
                await runtime.InteractiveSupervisor.RunAsync(CreateRequest(options, apiKey), appCts.Token).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (appCts.IsCancellationRequested)
            {
                return 0;
            }
            finally
            {
                if (cancelHandler is not null)
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }
        }
    }
}
