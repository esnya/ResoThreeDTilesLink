using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThreeDTilesLink.App;
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
            using IHost host = CreateHost(options);
            string apiKey = host.Services.GetRequiredService<IOptions<GoogleMapsOptions>>().Value.ApiKey;
            TileStreamingRuntime runtime = await TileStreamingRuntimeFactory.CreateAsync(
                host.Services.GetRequiredService<ILoggerFactory>(),
                host.Services.GetRequiredService<IOptions<RuntimeOptions>>()).ConfigureAwait(false);
            await using (runtime.ConfigureAwait(false))
            {
                return await executeAsync(
                    options,
                    runtime,
                    apiKey,
                    output,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static IHost CreateHost<TOptions>(TOptions options)
            where TOptions : ICommandRuntimeOptions
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            _ = builder.Logging.ClearProviders();
            _ = builder.Logging.SetMinimumLevel(options.LogLevel);
            _ = builder.Logging.AddSimpleConsole(consoleOptions =>
            {
                consoleOptions.IncludeScopes = false;
                consoleOptions.SingleLine = true;
                consoleOptions.TimestampFormat = "HH:mm:ss ";
            });

            _ = builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:ContentWorkers"] = options.ContentWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Runtime:ResoniteSendWorkers"] = options.ResoniteSendWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Runtime:TimeoutSec"] = options.TimeoutSec.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Runtime:MeasurePerformance"] = options.MeasurePerformance.ToString(),
                ["Runtime:LogLevel"] = options.LogLevel.ToString(),
                ["GoogleMaps:ApiKey"] = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY")
            });

            _ = builder.Services
                .AddOptions<RuntimeOptions>()
                .Bind(builder.Configuration.GetSection("Runtime"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            _ = builder.Services
                .AddOptions<GoogleMapsOptions>()
                .Bind(builder.Configuration.GetSection("GoogleMaps"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return builder.Build();
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
