using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.App;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;

namespace ThreeDTilesLink
{
    internal static class CommandHost
    {
        internal static async Task<int> RunAsync<TOptions>(
            IReadOnlyList<string> args,
            Func<IReadOnlyList<string>, CommandInvocation<TOptions>> parse,
            TextWriter output)
            where TOptions : class, ICommandRuntimeOptions
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(parse);
            ArgumentNullException.ThrowIfNull(output);

            CommandInvocation<TOptions> invocation = parse(args);
            if (!invocation.ShouldRun)
            {
                await WriteOutputAsync(output, invocation.Output, invocation.WriteToError).ConfigureAwait(false);
                return invocation.ExitCode;
            }

            TOptions options = invocation.Options!;
            using IHost host = CreateHost(options, output);
            CommandCompletion completion = host.Services.GetRequiredService<CommandCompletion>();
            await host.RunAsync().ConfigureAwait(false);
            return await completion.Completion.ConfigureAwait(false);
        }

        private static IHost CreateHost<TOptions>(TOptions options, TextWriter output)
            where TOptions : class, ICommandRuntimeOptions
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
                ["GoogleMaps:ApiKey"] = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY")
            });

            _ = builder.Services
                .AddOptions<GoogleMapsOptions>()
                .Bind(builder.Configuration.GetSection("GoogleMaps"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            _ = builder.Services.AddSingleton(options);
            _ = builder.Services.AddSingleton(output);
            _ = builder.Services.AddSingleton<CommandCompletion>();
            _ = builder.Services.AddThreeDTilesLinkRuntime(options);
            _ = builder.Services.AddThreeDTilesLinkCommandHost(options);

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
