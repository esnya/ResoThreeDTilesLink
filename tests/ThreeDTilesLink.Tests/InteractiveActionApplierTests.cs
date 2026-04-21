using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.Tests
{
    public sealed class InteractiveActionApplierTests
    {
        [Fact]
        public async Task ApplyAsync_RequeuesSearchFromFailureTime_WithoutDroppingPendingValues()
        {
            DateTimeOffset failureTime = DateTimeOffset.UnixEpoch.AddSeconds(42);
            var clock = new FakeClock(failureTime);
            var applier = new InteractiveActionApplier(
                new StubTileSelectionService(),
                new StubResoniteSession(),
                new ThrowingInteractiveInputStore(new HttpRequestException("synthetic input write failure")),
                new FixedSearchResolver(new LocationSearchResult("Shibuya", 35.65858d, 139.745433d)),
                new PassThroughTransformer(),
                clock,
                NullLoggerFactory.Instance);
            InteractiveLoopState state = InteractiveLoopState.CreateInitial() with
            {
                InputBinding = CreateInputBinding(),
                PendingValues = new SelectionInputValues(10f, 20f, 30f),
                PendingValuesChangedAt = DateTimeOffset.UnixEpoch
            };

            InteractiveLoopState next = await applier.ApplyAsync(
                state,
                [new ResolveSearchAction("Shibuya")],
                CreateOptions(),
                CancellationToken.None);

            _ = next.PendingSearch.Should().Be("Shibuya");
            _ = next.PendingSearchChangedAt.Should().Be(failureTime);
            _ = next.PendingValues.Should().Be(new SelectionInputValues(10f, 20f, 30f));
            _ = next.PendingValuesChangedAt.Should().Be(DateTimeOffset.UnixEpoch);
        }

        private static InteractiveRunRequest CreateOptions()
        {
            return new InteractiveRunRequest(
                "localhost",
                12345,
                0d,
                new TraversalOptions(400d, 30d),
                new TileSourceOptions(
                    new Uri("https://example.com/root.json"),
                    new TileSourceAccess("k", null)),
                Search: new SearchOptions("k"),
                RemoveOutOfRange: false,
                new WatchOptions(
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(1)));
        }

        private static InteractiveInputBinding CreateInputBinding()
        {
            return new InteractiveInputBinding(
                "lat", "Value", "latAlias", "Value",
                "lon", "Value", "lonAlias", "Value",
                "range", "Value", "rangeAlias", "Value",
                "search", "Value", "searchAlias", "Value");
        }

        private sealed class FakeClock(DateTimeOffset utcNow) : IClock
        {
            public DateTimeOffset UtcNow { get; set; } = utcNow;

            public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class StubTileSelectionService : ITileSelectionService
        {
            public Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<InteractiveTileRunResult> RunInteractiveAsync(
                TileRunRequest request,
                InteractiveRunInput interactive,
                CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class StubResoniteSession : IResoniteSession
        {
            public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
            public Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class ThrowingInteractiveInputStore(Exception exception) : IInteractiveInputStore
        {
            private readonly Exception _exception = exception;

            public Task<InteractiveInputBinding> CreateInteractiveInputBindingAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<SelectionInputValues?> ReadInteractiveInputValuesAsync(InteractiveInputBinding binding, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<string?> ReadInteractiveInputSearchAsync(InteractiveInputBinding binding, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task UpdateInteractiveInputCoordinatesAsync(InteractiveInputBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
            {
                return Task.FromException(_exception);
            }
        }

        private sealed class FixedSearchResolver(LocationSearchResult result) : ISearchResolver
        {
            private readonly LocationSearchResult _result = result;

            public Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken)
            {
                return Task.FromResult<LocationSearchResult?>(_result);
            }
        }

        private sealed class PassThroughTransformer : ICoordinateTransformer
        {
            public Vector3d GeographicToEcef(double latitudeDeg, double longitudeDeg, double height) => new(latitudeDeg, longitudeDeg, height);
            public Vector3d EcefToEnu(Vector3d ecef, GeoReference reference) => ecef;
            public Vector3d EnuToEun(Vector3d enu) => enu;
        }
    }
}
