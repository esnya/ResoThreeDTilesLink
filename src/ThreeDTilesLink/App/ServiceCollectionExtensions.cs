using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Mesh;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Resonite;
using ThreeDTilesLink.Core.Runtime;
using ThreeDTilesLink.Core.Tiles;
using ResoniteLink;

namespace ThreeDTilesLink.App
{
    internal static class ServiceCollectionExtensions
    {
        internal static IServiceCollection AddThreeDTilesLinkRuntime(
            this IServiceCollection services,
            ICommandRuntimeOptions options)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);

            if (options.MeasurePerformance)
            {
                _ = services.AddSingleton<RunPerformanceSummary>();
            }

            _ = services.AddSingleton<ICoordinateTransformer, GeographicCoordinateTransformer>();
            _ = services.AddSingleton<IGeoReferenceResolver, SeaLevelGeoReferenceResolver>();
            _ = services.AddSingleton<ITileSelector, TileSelector>();
            _ = services.AddSingleton<TraversalCore>();
            _ = services.AddSingleton<ResoniteReconcilerCore>();
            _ = services.AddSingleton<IGlbMeshExtractor, GlbMeshExtractor>();
            _ = services.AddSingleton<IMeshPlacementService, MeshPlacementService>();
            _ = services.AddSingleton<IContentProcessor, TileContentProcessor>();
            _ = services.AddSingleton<ISearchResolver, SearchResolver>();
            _ = services.AddSingleton<IClock, SystemClock>();
            _ = services.AddSingleton<SelectionInputReader>();
            _ = services.AddSingleton<InteractiveRunSupervisor>();

            _ = services.AddHttpClient<HttpTilesSource>((_, client) => ConfigureHttpClient(client, options))
                .ConfigurePrimaryHttpMessageHandler(() => CreateHttpHandler(options));
            _ = services.AddSingleton<ITilesSource>(static provider => provider.GetRequiredService<HttpTilesSource>());

            _ = services.AddHttpClient<GoogleGeocodingClient>((_, client) => ConfigureHttpClient(client, options))
                .ConfigurePrimaryHttpMessageHandler(() => CreateHttpHandler(options));

            _ = services.AddSingleton<ResoniteSession>(provider => new ResoniteSession(
                new LinkInterface(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResoniteSession>>(),
                assetImportWorkers: options.ResoniteSendWorkers));
            _ = services.AddSingleton<IResoniteSession>(static provider => provider.GetRequiredService<ResoniteSession>());
            _ = services.AddSingleton<IResoniteSessionMetadataPort>(static provider => provider.GetRequiredService<ResoniteSession>());
            _ = services.AddSingleton<IInteractiveInputStore>(static provider => provider.GetRequiredService<ResoniteSession>());

            _ = services.AddSingleton<ITileSelectionService>(provider => new TileSelectionService(
                provider.GetRequiredService<ITilesSource>(),
                provider.GetRequiredService<TraversalCore>(),
                provider.GetRequiredService<ResoniteReconcilerCore>(),
                provider.GetRequiredService<IContentProcessor>(),
                provider.GetRequiredService<IMeshPlacementService>(),
                provider.GetRequiredService<IResoniteSession>(),
                provider.GetRequiredService<IResoniteSessionMetadataPort>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TileSelectionService>>(),
                options.ContentWorkers,
                options.ResoniteSendWorkers,
                provider.GetService<RunPerformanceSummary>()));

            return services;
        }

        [SuppressMessage(
            "Design",
            "CA2263:Prefer generic overload when type is known",
            Justification = "Runtime command registration is selected from the parsed command option type.")]
        internal static IServiceCollection AddThreeDTilesLinkCommandHost<TOptions>(
            this IServiceCollection services,
            TOptions options)
            where TOptions : class, ICommandRuntimeOptions
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);

            if (options is StreamCommandOptions)
            {
                _ = services.AddHostedService<StreamCommandHostedService>();
                return services;
            }

            if (options is InteractiveCommandOptions)
            {
                _ = services.AddHostedService<InteractiveCommandHostedService>();
                return services;
            }

            throw new NotSupportedException($"Unsupported command option type: {typeof(TOptions).FullName}");
        }

        private static void ConfigureHttpClient(HttpClient client, ICommandRuntimeOptions options)
        {
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSec);
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        }

        private static SocketsHttpHandler CreateHttpHandler(ICommandRuntimeOptions options)
        {
            return new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli,
                EnableMultipleHttp2Connections = true,
                MaxConnectionsPerServer = Math.Max(32, options.ContentWorkers * 4),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
        }
    }
}
