using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ThreeDTilesLink.Core.App;
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
            ICommandRuntimeOptions runtimeOptions,
            TileSourceOptions tileSourceOptions,
            ResoniteDestinationPolicyOptions destinationPolicyOptions,
            SearchOptions searchOptions)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(runtimeOptions);
            ArgumentNullException.ThrowIfNull(tileSourceOptions);
            ArgumentNullException.ThrowIfNull(destinationPolicyOptions);
            ArgumentNullException.ThrowIfNull(searchOptions);

            _ = services.AddSingleton(runtimeOptions);
            _ = services.AddSingleton(tileSourceOptions);
            _ = services.AddSingleton(destinationPolicyOptions);
            _ = services.AddSingleton(searchOptions);

            _ = services.AddSingleton<ICoordinateTransformer, GeographicCoordinateTransformer>();
            _ = services.AddSingleton<IGeoReferenceResolver, SeaLevelGeoReferenceResolver>();
            _ = services.AddSingleton<ITileSelector, TileSelector>();
            _ = services.AddSingleton<TraversalCore>();
            _ = services.AddSingleton<ResoniteReconcilerCore>();
            _ = services.AddSingleton<IGlbMeshExtractor, GlbMeshExtractor>();
            _ = services.AddSingleton<IMeshPlacementService, MeshPlacementService>();
            _ = services.AddSingleton<ISearchResolver, SearchResolver>();
            _ = services.AddSingleton<IClock, SystemClock>();
            _ = services.AddSingleton<SelectionInputReader>();
            _ = services.AddSingleton<InteractiveRunSupervisor>();
            _ = services.AddSingleton<ITilesetParser, TilesetParser>();
            _ = services.AddSingleton<ILicenseCreditPolicy, GoogleTileLicenseCreditPolicy>();

            _ = services.AddHttpClient<HttpTilesSource>((_, client) => ConfigureHttpClient(client, runtimeOptions))
                .ConfigurePrimaryHttpMessageHandler(() => CreateHttpHandler(runtimeOptions));
            _ = services.AddSingleton<ITilesSource>(static provider => provider.GetRequiredService<HttpTilesSource>());

            _ = services.AddHttpClient<GoogleGeocodingClient>((_, client) => ConfigureHttpClient(client, runtimeOptions))
                .ConfigurePrimaryHttpMessageHandler(() => CreateHttpHandler(runtimeOptions));

            _ = services.AddSingleton<ResoniteSession>(provider => new ResoniteSession(
                new LinkInterface(),
                provider.GetRequiredService<ILicenseCreditPolicy>(),
                destinationPolicyOptions,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResoniteSession>>(),
                assetImportWorkers: runtimeOptions.ResoniteSendWorkers));
            _ = services.AddSingleton<IResoniteSession>(static provider => provider.GetRequiredService<ResoniteSession>());
            _ = services.AddSingleton<IResoniteSessionMetadataPort>(static provider => provider.GetRequiredService<ResoniteSession>());
            _ = services.AddSingleton<IInteractiveInputStore>(static provider => provider.GetRequiredService<ResoniteSession>());

            _ = services.AddSingleton<ITileSelectionService>(provider => new TileSelectionService(
                provider.GetRequiredService<ITilesSource>(),
                provider.GetRequiredService<TraversalCore>(),
                provider.GetRequiredService<ResoniteReconcilerCore>(),
                provider.GetRequiredService<IGlbMeshExtractor>(),
                provider.GetRequiredService<IMeshPlacementService>(),
                provider.GetRequiredService<IResoniteSession>(),
                provider.GetRequiredService<IResoniteSessionMetadataPort>(),
                provider.GetRequiredService<ILicenseCreditPolicy>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TileSelectionService>>(),
                runtimeOptions.ContentWorkers,
                runtimeOptions.ResoniteSendWorkers,
                provider.GetService<RunPerformanceSummary>()));

            return services;
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
