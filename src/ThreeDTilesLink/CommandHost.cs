using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.App;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink
{
    internal static class CommandHost
    {
        internal static async Task<int> RunAsync(
            RootCommandRoute route,
            TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(route);
            ArgumentNullException.ThrowIfNull(output);

            CommandRegistration registration = ResolveRegistration(route.Command);
            CommandInvocation<ICommandRuntimeOptions> invocation = registration.Parse(route.Arguments);
            if (!invocation.ShouldRun)
            {
                await WriteOutputAsync(output, invocation.Output, invocation.WriteToError).ConfigureAwait(false);
                return invocation.ExitCode;
            }

            ICommandRuntimeOptions options = invocation.Options!;
            using IHost host = CreateHost(registration, options, output);
            CommandCompletion completion = host.Services.GetRequiredService<CommandCompletion>();
            await host.RunAsync().ConfigureAwait(false);
            return await completion.Completion.ConfigureAwait(false);
        }

        private static IHost CreateHost(
            CommandRegistration registration,
            ICommandRuntimeOptions options,
            TextWriter output)
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
            TileSourceOptions tileSourceOptions = BuildTileSourceOptions(builder.Configuration);
            SearchOptions searchOptions = BuildSearchOptions(builder.Configuration);

            if (options.MeasurePerformance)
            {
                _ = builder.Services.AddSingleton<RunPerformanceSummary>();
            }

            _ = builder.Services.AddSingleton(registration.OptionsType, options);
            _ = builder.Services.AddSingleton(output);
            _ = builder.Services.AddSingleton<CommandCompletion>();
            _ = builder.Services.AddThreeDTilesLinkRuntime(
                options,
                tileSourceOptions,
                ResoniteDestinationPolicyOptions.CreateGoogleDefaults(),
                searchOptions);
            registration.Register(builder.Services);

            return builder.Build();
        }

        private static TileSourceOptions BuildTileSourceOptions(ConfigurationManager configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            Uri googleRootTilesetUri = new("https://tile.googleapis.com/v1/3dtiles/root.json");
            Uri googleFileSchemeBaseUri = new("https://tile.googleapis.com/");
            string? sharedGoogleApiKey = ResolveSharedGoogleApiKey(configuration);
            string[] inheritedQueryParameters = ReadList(
                configuration,
                "TileSource:InheritedQueryParameters",
                "TILE_SOURCE_INHERITED_QUERY_PARAMETERS",
                "session");
            string rootTilesetUriText = configuration["TileSource:RootTilesetUri"] ??
                configuration["TILE_SOURCE_ROOT_TILESET_URI"] ??
                googleRootTilesetUri.AbsoluteUri;
            if (!Uri.TryCreate(rootTilesetUriText, UriKind.Absolute, out Uri? rootTilesetUri))
            {
                throw new InvalidOperationException($"Tile source root URI must be absolute: {rootTilesetUriText}");
            }

            string? fileSchemeBaseUriText = configuration["TileSource:FileSchemeBaseUri"] ??
                configuration["TILE_SOURCE_FILE_SCHEME_BASE_URI"] ??
                googleFileSchemeBaseUri.AbsoluteUri;
            Uri? fileSchemeBaseUri = null;
            if (!string.IsNullOrWhiteSpace(fileSchemeBaseUriText))
            {
                if (!Uri.TryCreate(fileSchemeBaseUriText, UriKind.Absolute, out fileSchemeBaseUri))
                {
                    throw new InvalidOperationException($"Tile source file-scheme base URI must be absolute: {fileSchemeBaseUriText}");
                }
            }

            string[] normalizedInheritedQueryParameters = inheritedQueryParameters
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return new TileSourceOptions(
                rootTilesetUri,
                new TileSourceAccess(
                    configuration["TileSource:ApiKey"] ??
                    configuration["TILE_SOURCE_API_KEY"] ??
                    sharedGoogleApiKey,
                    configuration["TileSource:BearerToken"] ??
                    configuration["TILE_SOURCE_BEARER_TOKEN"]),
                new TileSourceContentLinkOptions(fileSchemeBaseUri, normalizedInheritedQueryParameters));
        }

        private static SearchOptions BuildSearchOptions(ConfigurationManager configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return new SearchOptions(
                configuration["Search:ApiKey"] ??
                configuration["SEARCH_API_KEY"] ??
                ResolveSharedGoogleApiKey(configuration));
        }

        private static string? ResolveSharedGoogleApiKey(ConfigurationManager configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration["GOOGLE_MAPS_API_KEY"] ??
                configuration["GoogleMaps:ApiKey"];
        }

        private static string[] ReadList(
            ConfigurationManager configuration,
            string sectionKey,
            string flatKey,
            params string[] fallbackValues)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            string? configured = configuration[sectionKey] ?? configuration[flatKey];
            if (string.IsNullOrWhiteSpace(configured))
            {
                return fallbackValues;
            }

            return configured
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static CommandRegistration ResolveRegistration(RootCommandKind command)
        {
            return command switch
            {
                RootCommandKind.Stream => new CommandRegistration(
                    typeof(StreamCommandOptions),
                    args =>
                    {
                        CommandInvocation<StreamCommandOptions> parsed = StreamCommandLine.Parse(args);
                        return new CommandInvocation<ICommandRuntimeOptions>(
                            parsed.ShouldRun,
                            parsed.Options,
                            parsed.ExitCode,
                            parsed.Output,
                            parsed.WriteToError);
                    },
                    services => services.AddHostedService<StreamCommandHostedService>()),
                RootCommandKind.Interactive => new CommandRegistration(
                    typeof(InteractiveCommandOptions),
                    args =>
                    {
                        CommandInvocation<InteractiveCommandOptions> parsed = InteractiveCommandLine.Parse(args);
                        return new CommandInvocation<ICommandRuntimeOptions>(
                            parsed.ShouldRun,
                            parsed.Options,
                            parsed.ExitCode,
                            parsed.Output,
                            parsed.WriteToError);
                    },
                    services => services.AddHostedService<InteractiveCommandHostedService>()),
                _ => throw new InvalidOperationException($"Unsupported command: {command}")
            };
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

        private sealed record CommandRegistration(
            Type OptionsType,
            Func<IReadOnlyList<string>, CommandInvocation<ICommandRuntimeOptions>> Parse,
            Action<IServiceCollection> Register);
    }
}
