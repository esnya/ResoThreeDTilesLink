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
        public async Task TryReadInteractiveInputSearchAsync_ReturnsNull_AndDoesNotWarn_WhenResponseIsMissing()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ThrowingInteractiveInputStore(new ResoniteLinkNoResponseException(), null),
                logger);

            string? result = await monitor.TryReadInteractiveInputSearchAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Debug);
            _ = logger.Entries[0].Message.Should().Contain("read returned no response");
        }

        [Fact]
        public async Task TryReadInteractiveInputValuesAsync_ReturnsNull_AndDoesNotWarn_WhenResponseIsMissing()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ThrowingInteractiveInputStore(null, new ResoniteLinkNoResponseException()),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveInputValuesAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Debug);
            _ = logger.Entries[0].Message.Should().Be("Selection input read returned no response.");
        }

        [Fact]
        public async Task TryReadInteractiveInputValuesAsync_ReturnsNull_WhenRangeIsZero()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ValueInteractiveInputStore(new SelectionInputValues(35.0f, 139.0f, 0f)),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveInputValuesAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().BeEmpty();
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(-10f)]
        public async Task TryReadInteractiveInputValuesAsync_ReturnsNull_WhenRangeIsInvalid(float rangeM)
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ValueInteractiveInputStore(new SelectionInputValues(35.0f, 139.0f, rangeM)),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveInputValuesAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().BeEmpty();
        }

        [Theory]
        [InlineData(float.NaN, 139f)]
        [InlineData(float.PositiveInfinity, 139f)]
        [InlineData(-91f, 139f)]
        [InlineData(91f, 139f)]
        [InlineData(35f, float.NaN)]
        [InlineData(35f, float.NegativeInfinity)]
        [InlineData(35f, -181f)]
        [InlineData(35f, 181f)]
        public async Task TryReadInteractiveInputValuesAsync_ReturnsNull_WhenCoordinatesAreInvalid(float latitude, float longitude)
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ValueInteractiveInputStore(new SelectionInputValues(latitude, longitude, 100f)),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveInputValuesAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().BeEmpty();
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void QueryRange_RejectsNonFinite(double rangeM)
        {
            Action act = () => _ = new QueryRange(rangeM);
            _ = act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void QueryRange_RejectsNonPositive()
        {
            Action act = () => _ = new QueryRange(0d);
            _ = act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void QueryRange_AcceptsValidPositiveRange()
        {
            var range = new QueryRange(50d);
            _ = range.Min.Should().Be(-50d);
            _ = range.Max.Should().Be(50d);
        }

        private static InteractiveInputBinding CreateInputBinding()
        {
            return new InteractiveInputBinding(
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

        private sealed class ThrowingInteractiveInputStore(Exception? searchException, Exception? valuesException) : IInteractiveInputStore
        {
            private readonly Exception? _searchException = searchException;
            private readonly Exception? _valuesException = valuesException;

            public Task<InteractiveInputBinding> CreateInteractiveInputBindingAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<SelectionInputValues?> ReadInteractiveInputValuesAsync(InteractiveInputBinding binding, CancellationToken cancellationToken)
            {
                return _valuesException is null
                    ? Task.FromResult<SelectionInputValues?>(null)
                    : Task.FromException<SelectionInputValues?>(_valuesException);
            }

            public Task<string?> ReadInteractiveInputSearchAsync(InteractiveInputBinding binding, CancellationToken cancellationToken)
            {
                return _searchException is null
                    ? Task.FromResult<string?>(null)
                    : Task.FromException<string?>(_searchException);
            }

            public Task UpdateInteractiveInputCoordinatesAsync(InteractiveInputBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class ValueInteractiveInputStore(SelectionInputValues values) : IInteractiveInputStore
        {
            public Task<InteractiveInputBinding> CreateInteractiveInputBindingAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<SelectionInputValues?> ReadInteractiveInputValuesAsync(InteractiveInputBinding binding, CancellationToken cancellationToken)
            {
                return Task.FromResult<SelectionInputValues?>(values);
            }

            public Task<string?> ReadInteractiveInputSearchAsync(InteractiveInputBinding binding, CancellationToken cancellationToken)
            {
                return Task.FromResult<string?>(null);
            }

            public Task UpdateInteractiveInputCoordinatesAsync(InteractiveInputBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
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
