using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Runtime;

namespace ThreeDTilesLink
{
    internal static class CommandHost
    {
        internal static async Task<int> RunAsync<TOptions>(
            IReadOnlyList<string> args,
            Func<IReadOnlyList<string>, CommandInvocation<TOptions>> parse,
            Func<TOptions, TileStreamingRuntime, string, TextWriter, CancellationToken, Task<int>> executeAsync,
            TextWriter output,
            CancellationToken cancellationToken)
            where TOptions : ICommandRuntimeOptions
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(parse);
            ArgumentNullException.ThrowIfNull(executeAsync);
            ArgumentNullException.ThrowIfNull(output);

            CommandInvocation<TOptions> invocation = parse(args);
            if (!invocation.ShouldRun)
            {
                await WriteOutputAsync(output, invocation.Output, invocation.WriteToError).ConfigureAwait(false);
                return invocation.ExitCode;
            }

            TOptions options = invocation.Options!;
            string apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? string.Empty;
#pragma warning disable CA2000
            TileStreamingRuntimeHandle runtimeHandle = TileStreamingRuntimeFactory.Create(
                options.LogLevel,
                TimeSpan.FromSeconds(options.TimeoutSec),
                options.ContentWorkers,
                options.ResoniteSendWorkers,
                options.MeasurePerformance);
#pragma warning restore CA2000
            await using (runtimeHandle.ConfigureAwait(false))
            {
                return await executeAsync(
                    options,
                    runtimeHandle.Runtime,
                    apiKey,
                    output,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        internal static async Task WriteOutputAsync(TextWriter output, string text, bool writeToError)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            TextWriter target = writeToError ? Console.Error : output;
            await target.WriteLineAsync(text).ConfigureAwait(false);
        }
    }
}
