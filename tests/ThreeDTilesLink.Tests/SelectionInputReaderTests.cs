using FluentAssertions;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Tests
{
    public sealed class SelectionInputReaderTests
    {
        [Fact]
        public async Task TryReadWatchSearchAsync_ReturnsNull_AndDoesNotWarn_WhenResponseIsMissing()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ThrowingWatchStore(new ResoniteLinkNoResponseException(), null),
                logger);

            string? result = await monitor.TryReadWatchSearchAsync(CreateBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Debug);
            _ = logger.Entries[0].Message.Should().Be("Watch search query read returned no response.");
        }

        [Fact]
        public async Task TryReadSelectionInputValuesAsync_ReturnsNull_AndDoesNotWarn_WhenResponseIsMissing()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ThrowingWatchStore(null, new ResoniteLinkNoResponseException()),
                logger);

            SelectionInputValues? result = await monitor.TryReadSelectionInputValuesAsync(CreateBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Debug);
            _ = logger.Entries[0].Message.Should().Be("Selection input read returned no response.");
        }

        private static WatchBinding CreateBinding()
        {
            return new WatchBinding(
                "lat",
                "Value",
                "lat_alias",
                "Value",
                "lon",
                "Value",
                "lon_alias",
                "Value",
                "range",
                "Value",
                "range_alias",
                "Value",
                "search",
                "Value",
                "search_alias",
                "Value");
        }

        private sealed class ThrowingWatchStore(Exception? searchException, Exception? valuesException) : IWatchStore
        {
            private readonly Exception? _searchException = searchException;
            private readonly Exception? _valuesException = valuesException;

            public Task<WatchBinding> CreateWatchAsync(WatchConfiguration configuration, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<SelectionInputValues?> ReadSelectionInputValuesAsync(WatchBinding binding, CancellationToken cancellationToken)
            {
                return _valuesException is null
                    ? Task.FromResult<SelectionInputValues?>(null)
                    : Task.FromException<SelectionInputValues?>(_valuesException);
            }

            public Task<string?> ReadWatchSearchAsync(WatchBinding binding, CancellationToken cancellationToken)
            {
                return _searchException is null
                    ? Task.FromResult<string?>(null)
                    : Task.FromException<string?>(_searchException);
            }

            public Task UpdateWatchCoordinatesAsync(WatchBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class ListLogger<T> : ILogger<T>
        {
            public List<LogEntry> Entries { get; } = [];

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
            {
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }

        private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
