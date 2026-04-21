using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.WebSockets;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.Tests
{
    public sealed class InteractiveSearchCoordinatorTests
    {
        [Fact]
        public async Task ResolveSearchAsync_RequeuesSearchFromFailureTime_WithoutDroppingPendingValues()
        {
            DateTimeOffset failureTime = DateTimeOffset.UnixEpoch.AddSeconds(42);
            var clock = new FakeClock(failureTime);
            var coordinator = new InteractiveSearchCoordinator(
                new ThrowingInteractiveUiStore(new HttpRequestException("synthetic input write failure")),
                new FixedSearchResolver(new LocationSearchResult("Shibuya", 35.65858d, 139.745433d)),
                clock,
                NullLogger<InteractiveSearchCoordinator>.Instance);
            InteractiveLoopState state = InteractiveLoopState.CreateInitial() with
            {
                InputBinding = CreateInputBinding(),
                PendingValues = new SelectionInputValues(10f, 20f, 30f),
                PendingValuesChangedAt = DateTimeOffset.UnixEpoch
            };

            InteractiveLoopState next = await coordinator.ResolveSearchAsync(
                state,
                CreateOptions(),
                new ResolveSearchAction("Shibuya"),
                CancellationToken.None);

            _ = next.PendingSearch.Should().Be("Shibuya");
            _ = next.PendingSearchChangedAt.Should().Be(failureTime);
            _ = next.PendingValues.Should().Be(new SelectionInputValues(10f, 20f, 30f));
            _ = next.PendingValuesChangedAt.Should().Be(DateTimeOffset.UnixEpoch);
        }

        [Theory]
        [InlineData(typeof(InteractiveUiNoResponseException))]
        [InlineData(typeof(InteractiveUiDisconnectedException))]
        [InlineData(typeof(WebSocketException))]
        public async Task ResolveSearchAsync_RequeuesSearch_WhenInteractiveUiWritebackFails(Type exceptionType)
        {
            DateTimeOffset failureTime = DateTimeOffset.UnixEpoch.AddSeconds(99);
            var clock = new FakeClock(failureTime);
            Exception exception = exceptionType == typeof(InteractiveUiNoResponseException)
                ? new InteractiveUiNoResponseException("synthetic")
                : exceptionType == typeof(InteractiveUiDisconnectedException)
                    ? new InteractiveUiDisconnectedException("synthetic")
                    : new WebSocketException();
            var coordinator = new InteractiveSearchCoordinator(
                new ThrowingInteractiveUiStore(exception),
                new FixedSearchResolver(new LocationSearchResult("Shibuya", 35.65858d, 139.745433d)),
                clock,
                NullLogger<InteractiveSearchCoordinator>.Instance);
            InteractiveLoopState state = InteractiveLoopState.CreateInitial() with
            {
                InputBinding = CreateInputBinding()
            };

            InteractiveLoopState next = await coordinator.ResolveSearchAsync(
                state,
                CreateOptions(),
                new ResolveSearchAction("Shibuya"),
                CancellationToken.None);

            _ = next.PendingSearch.Should().Be("Shibuya");
            _ = next.PendingSearchChangedAt.Should().Be(failureTime);
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

        private static InteractiveUiBinding CreateInputBinding()
        {
            return new InteractiveUiBinding("binding");
        }

        private sealed class FakeClock(DateTimeOffset utcNow) : IClock
        {
            public DateTimeOffset UtcNow { get; set; } = utcNow;

            public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingInteractiveUiStore(Exception exception) : IInteractiveUiStore
        {
            private readonly Exception _exception = exception;

            public Task<InteractiveUiBinding> CreateInteractiveUiBindingAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<SelectionInputValues?> ReadInteractiveUiValuesAsync(InteractiveUiBinding binding, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<string?> ReadInteractiveUiSearchAsync(InteractiveUiBinding binding, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task UpdateInteractiveUiCoordinatesAsync(InteractiveUiBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
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
    }
}
