using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Runtime;

namespace ThreeDTilesLink.Core.App
{
    internal static class ConsoleInteractiveHost
    {
        internal static async Task<int> RunAsync(
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
                    _ = appCts.CancelAsync();
                };
                Console.CancelKeyPress += cancelHandler;

                await output.WriteLineAsync(
                    $"Interactive mode started. Input=session root slot values (Latitude/Longitude/Range/Search). Poll={options.PollIntervalMs}ms Debounce={options.DebounceMs}ms Throttle={options.ThrottleMs}ms. Press Ctrl+C to stop.")
                    .ConfigureAwait(false);
                await runtime.InteractiveSupervisor.RunAsync(
                    InteractiveCommandHandler.CreateRequest(options, apiKey),
                    appCts.Token).ConfigureAwait(false);
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
