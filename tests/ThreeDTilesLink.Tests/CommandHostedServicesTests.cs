using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThreeDTilesLink.App;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Google;
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
                Options.Create(new GoogleMapsOptions { ApiKey = "key" }),
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
                Options.Create(new GoogleMapsOptions { ApiKey = "key" }),
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
            _ = services.AddSingleton<ICommandRuntimeOptions>(new InteractiveCommandOptions(
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
                LogLevel.Information));

            _ = services.AddThreeDTilesLinkRuntime(new InteractiveCommandOptions(
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
                LogLevel.Information));

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

            MethodInfo? createHostDefinition = typeof(ThreeDTilesLink.CommandHost).GetMethod(
                "CreateHost",
                BindingFlags.Static | BindingFlags.NonPublic);
            _ = createHostDefinition.Should().NotBeNull();
            MethodInfo createHost = createHostDefinition!.MakeGenericMethod(typeof(InteractiveCommandOptions));

            using IHost host = (IHost)createHost.Invoke(null, [options, TextWriter.Null])!;

            Func<IEnumerable<IHostedService>> act = () => host.Services.GetRequiredService<IEnumerable<IHostedService>>();

            IEnumerable<IHostedService> services = act.Should().NotThrow().Subject;
            _ = services.Should().ContainSingle(static service => service is InteractiveCommandHostedService);
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
                    "Value",
                    "latAlias",
                    "Value",
                    "lonComponent",
                    "Value",
                    "lonAlias",
                    "Value",
                    "rangeComponent",
                    "Value",
                    "rangeAlias",
                    "Value",
                    "searchComponent",
                    "Value",
                    "searchAlias",
                    "Value"));
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
