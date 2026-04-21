using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.App;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.Tests
{
    public sealed class CommandHostedServicesTests
    {
        [Fact]
        public async Task StreamExecuteAsync_FaultsCompletion_WhenTileSelectionFails()
        {
            var expected = new InvalidOperationException("boom");
            var completion = new CommandCompletion();
            var lifetime = new FakeHostApplicationLifetime();
            var tileSource = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json"),
                new TileSourceAccess(null, null));
            using var service = new StreamCommandHostedService(
                new StreamCommandOptions(
                    35.65858d,
                    139.745433d,
                    20d,
                    60d,
                    "localhost",
                    4301,
                    25d,
                    4,
                    2,
                    90,
                    false,
                    false,
                    LogLevel.Information),
                new ThrowingTileSelectionService(expected),
                new FakeGeoReferenceResolver(),
                tileSource,
                TextWriter.Null,
                completion,
                lifetime);

            Task executeTask = InvokeExecuteAsync(service, CancellationToken.None);

            _ = await FluentActions.Awaiting(() => completion.Completion).Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("boom");
            _ = await FluentActions.Awaiting(() => executeTask).Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("boom");
            _ = lifetime.StopApplicationCalls.Should().Be(1);
        }

        [Fact]
        public async Task InteractiveExecuteAsync_FaultsCompletion_WhenSupervisorFails()
        {
            var expected = new InvalidOperationException("boom");
            var completion = new CommandCompletion();
            var lifetime = new FakeHostApplicationLifetime();
            var inputStore = new FakeInteractiveInputStore();
            var tileSource = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json"),
                new TileSourceAccess(null, null));
            using var service = new InteractiveCommandHostedService(
                new InteractiveCommandOptions(
                    20d,
                    "localhost",
                    4301,
                    25d,
                    4,
                    2,
                    90,
                    false,
                    250,
                    800,
                    3000,
                    LogLevel.Information),
                new InteractiveRunSupervisor(
                    new ThrowingTileSelectionService(expected),
                    new ThrowingResoniteSession(expected),
                    inputStore,
                new FakeSearchResolver(),
                new FakeCoordinateTransformer(),
                new FakeGeoReferenceResolver(),
                new FakeClock(),
                new SelectionInputReader(inputStore, NullLogger<SelectionInputReader>.Instance),
                NullLoggerFactory.Instance,
                NullLogger<InteractiveRunSupervisor>.Instance),
                tileSource,
                new SearchOptions("key"),
                TextWriter.Null,
                completion,
                lifetime);

            Task executeTask = InvokeExecuteAsync(service, CancellationToken.None);

            _ = await FluentActions.Awaiting(() => completion.Completion).Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("boom");
            _ = await FluentActions.Awaiting(() => executeTask).Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("boom");
            _ = lifetime.StopApplicationCalls.Should().Be(1);
        }

        [Fact]
        public async Task AddThreeDTilesLinkRuntime_ResolvesInteractiveRunSupervisor()
        {
            var services = new ServiceCollection();
            _ = services.AddLogging();
            var runtimeOptions = new InteractiveCommandOptions(
                20d,
                "localhost",
                4301,
                25d,
                4,
                2,
                90,
                false,
                250,
                800,
                3000,
                LogLevel.Information);
            var tileSource = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json"),
                new TileSourceAccess("key", null));
            ResoniteDestinationPolicyOptions destinationPolicy = ResoniteDestinationPolicyOptions.CreateGoogleDefaults();

            _ = services.AddThreeDTilesLinkRuntime(
                runtimeOptions,
                tileSource,
                destinationPolicy,
                new SearchOptions("key"));
            await using ServiceProvider provider = services.BuildServiceProvider();

            Func<InteractiveRunSupervisor> act = () => provider.GetRequiredService<InteractiveRunSupervisor>();

            _ = act.Should().NotThrow();
        }

        [Fact]
        public void CommandHost_CreateHost_ResolvesInteractiveHostedService()
        {
            var options = new InteractiveCommandOptions(
                20d,
                "localhost",
                4301,
                25d,
                4,
                2,
                90,
                false,
                250,
                800,
                3000,
                LogLevel.Information);

            Type commandHostType = typeof(ThreeDTilesLink.CommandHost);
            MethodInfo? resolveRegistrationDefinition = commandHostType.GetMethod(
                "ResolveRegistration",
                BindingFlags.Static | BindingFlags.NonPublic);
            _ = resolveRegistrationDefinition.Should().NotBeNull();
            MethodInfo? createHostDefinition = commandHostType.GetMethod(
                "CreateHost",
                BindingFlags.Static | BindingFlags.NonPublic);
            _ = createHostDefinition.Should().NotBeNull();
            object commandKind = Enum.Parse(
                resolveRegistrationDefinition!.GetParameters()[0].ParameterType,
                "Interactive");
            object registration = resolveRegistrationDefinition.Invoke(null, [commandKind])!;

            using IHost host = (IHost)createHostDefinition!.Invoke(
                null,
                [registration, options, TextWriter.Null])!;

            Func<IEnumerable<IHostedService>> act = () => host.Services.GetRequiredService<IEnumerable<IHostedService>>();

            IEnumerable<IHostedService> services = act.Should().NotThrow().Subject;
            _ = services.Should().ContainSingle(static service => service is InteractiveCommandHostedService);
        }

        [Fact]
        public void CommandHost_CreateHost_RegistersConfiguredTileAndSearchOptions()
        {
            const string rootUri = "https://plateau.example.com/tiles/root.json";
            const string fileBaseUri = "https://cdn.plateau.example.com/";
            const string tileApiKey = "tile-key";
            const string searchApiKey = "search-key";
            var options = new InteractiveCommandOptions(
                20d,
                "localhost",
                4301,
                25d,
                4,
                2,
                90,
                false,
                250,
                800,
                3000,
                LogLevel.Information);

            string? previousRoot = Environment.GetEnvironmentVariable("TILE_SOURCE_ROOT_TILESET_URI");
            string? previousFileBase = Environment.GetEnvironmentVariable("TILE_SOURCE_FILE_SCHEME_BASE_URI");
            string? previousTileApiKey = Environment.GetEnvironmentVariable("TILE_SOURCE_API_KEY");
            string? previousSearchApiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY");
            string? previousInherited = Environment.GetEnvironmentVariable("TILE_SOURCE_INHERITED_QUERY_PARAMETERS");

            try
            {
                Environment.SetEnvironmentVariable("TILE_SOURCE_ROOT_TILESET_URI", rootUri);
                Environment.SetEnvironmentVariable("TILE_SOURCE_FILE_SCHEME_BASE_URI", fileBaseUri);
                Environment.SetEnvironmentVariable("TILE_SOURCE_API_KEY", tileApiKey);
                Environment.SetEnvironmentVariable("SEARCH_API_KEY", searchApiKey);
                Environment.SetEnvironmentVariable("TILE_SOURCE_INHERITED_QUERY_PARAMETERS", "session,sig");

                Type commandHostType = typeof(ThreeDTilesLink.CommandHost);
                MethodInfo? resolveRegistrationDefinition = commandHostType.GetMethod(
                    "ResolveRegistration",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo? createHostDefinition = commandHostType.GetMethod(
                    "CreateHost",
                    BindingFlags.Static | BindingFlags.NonPublic);
                _ = resolveRegistrationDefinition.Should().NotBeNull();
                _ = createHostDefinition.Should().NotBeNull();

                object commandKind = Enum.Parse(
                    resolveRegistrationDefinition!.GetParameters()[0].ParameterType,
                    "Interactive");
                object registration = resolveRegistrationDefinition.Invoke(null, [commandKind])!;

                using IHost host = (IHost)createHostDefinition!.Invoke(
                    null,
                    [registration, options, TextWriter.Null])!;

                TileSourceOptions tileSource = host.Services.GetRequiredService<TileSourceOptions>();
                SearchOptions search = host.Services.GetRequiredService<SearchOptions>();

                _ = tileSource.RootTilesetUri.AbsoluteUri.Should().Be(rootUri);
                _ = tileSource.Access.ApiKey.Should().Be(tileApiKey);
                _ = tileSource.ContentLinks.FileSchemeBaseUri!.AbsoluteUri.Should().Be(fileBaseUri);
                _ = tileSource.ContentLinks.InheritedQueryParameters.Should().Equal("session", "sig");
                _ = search.ApiKey.Should().Be(searchApiKey);
            }
            finally
            {
                Environment.SetEnvironmentVariable("TILE_SOURCE_ROOT_TILESET_URI", previousRoot);
                Environment.SetEnvironmentVariable("TILE_SOURCE_FILE_SCHEME_BASE_URI", previousFileBase);
                Environment.SetEnvironmentVariable("TILE_SOURCE_API_KEY", previousTileApiKey);
                Environment.SetEnvironmentVariable("SEARCH_API_KEY", previousSearchApiKey);
                Environment.SetEnvironmentVariable("TILE_SOURCE_INHERITED_QUERY_PARAMETERS", previousInherited);
            }
        }

        [Fact]
        public void CommandHost_BuildSearchOptions_PrefersLegacyGoogleMapsEnvVarOverSectionValue()
        {
            const string envApiKey = "google-env-key";
            const string configApiKey = "google-config-key";
            string? previousGoogleApiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");

            try
            {
                Environment.SetEnvironmentVariable("GOOGLE_MAPS_API_KEY", envApiKey);
                using var configuration = new ConfigurationManager();
                _ = configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GoogleMaps:ApiKey"] = configApiKey
                });
                _ = configuration.AddEnvironmentVariables();

                SearchOptions search = InvokeBuildSearchOptions(configuration);

                _ = search.ApiKey.Should().Be(envApiKey);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GOOGLE_MAPS_API_KEY", previousGoogleApiKey);
            }
        }

        [Fact]
        public void CommandHost_BuildTileSourceOptions_PrefersLegacyGoogleMapsEnvVarOverSectionValue()
        {
            const string envApiKey = "google-env-key";
            const string configApiKey = "google-config-key";
            string? previousGoogleApiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");

            try
            {
                Environment.SetEnvironmentVariable("GOOGLE_MAPS_API_KEY", envApiKey);
                using var configuration = new ConfigurationManager();
                _ = configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GoogleMaps:ApiKey"] = configApiKey
                });
                _ = configuration.AddEnvironmentVariables();

                TileSourceOptions tileSource = InvokeBuildTileSourceOptions(configuration);

                _ = tileSource.Access.ApiKey.Should().Be(envApiKey);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GOOGLE_MAPS_API_KEY", previousGoogleApiKey);
            }
        }

        private static Task InvokeExecuteAsync(object service, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(service);

            MethodInfo? method = service.GetType().GetMethod(
                "ExecuteAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _ = method.Should().NotBeNull();
            return (Task)method!.Invoke(service, [cancellationToken])!;
        }

        private static SearchOptions InvokeBuildSearchOptions(ConfigurationManager configuration)
        {
            MethodInfo? method = typeof(ThreeDTilesLink.CommandHost).GetMethod(
                "BuildSearchOptions",
                BindingFlags.Static | BindingFlags.NonPublic);
            _ = method.Should().NotBeNull();
            return (SearchOptions)method!.Invoke(null, [configuration])!;
        }

        private static TileSourceOptions InvokeBuildTileSourceOptions(ConfigurationManager configuration)
        {
            MethodInfo? method = typeof(ThreeDTilesLink.CommandHost).GetMethod(
                "BuildTileSourceOptions",
                BindingFlags.Static | BindingFlags.NonPublic);
            _ = method.Should().NotBeNull();
            return (TileSourceOptions)method!.Invoke(null, [configuration])!;
        }

        private sealed class ThrowingTileSelectionService(Exception exception) : ITileSelectionService
        {
            private readonly Exception _exception = exception;

            public async Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                throw _exception;
            }

            public Task<InteractiveTileRunResult> RunInteractiveAsync(
                TileRunRequest request,
                InteractiveRunInput interactive,
                CancellationToken cancellationToken)
            {
                return Task.FromException<InteractiveTileRunResult>(_exception);
            }
        }

        private sealed class ThrowingResoniteSession(Exception exception) : IResoniteSession
        {
            private readonly Exception _exception = exception;

            public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) => Task.FromException(_exception);

            public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

            public Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeGeoReferenceResolver : IGeoReferenceResolver
        {
            public GeoReference Resolve(double latitude, double longitude, double heightOffset)
            {
                return new GeoReference(latitude, longitude, 100d + heightOffset);
            }
        }

        private sealed class FakeInteractiveInputStore : IInteractiveInputStore
        {
            public Task<InteractiveInputBinding> CreateInteractiveInputBindingAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new InteractiveInputBinding(
                    "latComponent",
                    "latAlias",
                    "lonComponent",
                    "lonAlias",
                    "rangeComponent",
                    "rangeAlias",
                    "searchComponent",
                    "searchAlias"));
            }

            public Task<SelectionInputValues?> ReadInteractiveInputValuesAsync(InteractiveInputBinding binding, CancellationToken cancellationToken) => Task.FromResult<SelectionInputValues?>(null);

            public Task<string?> ReadInteractiveInputSearchAsync(InteractiveInputBinding binding, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

            public Task UpdateInteractiveInputCoordinatesAsync(InteractiveInputBinding binding, double latitude, double longitude, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeSearchResolver : ISearchResolver
        {
            public Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken) => Task.FromResult<LocationSearchResult?>(null);
        }

        private sealed class FakeCoordinateTransformer : ICoordinateTransformer
        {
            public Vector3d GeographicToEcef(double latitudeDeg, double longitudeDeg, double height) => default;

            public Vector3d EcefToEnu(Vector3d ecef, GeoReference reference) => default;

            public Vector3d EnuToEun(Vector3d enu) => default;
        }

        private sealed class FakeClock : IClock
        {
            public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

            public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;

            public CancellationToken ApplicationStopping => CancellationToken.None;

            public CancellationToken ApplicationStopped => CancellationToken.None;

            public int StopApplicationCalls { get; private set; }

            public void StopApplication()
            {
                StopApplicationCalls++;
            }
        }
    }
}
