using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
namespace ThreeDTilesLink.Tests
{
    public sealed class SelectionInputReaderTests
    {
        [Fact]
        public async Task TryReadInteractiveUiSearchAsync_ReturnsNull_AndDoesNotWarn_WhenResponseIsMissing()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ThrowingInteractiveUiStore(new InteractiveUiNoResponseException("missing", new TimeoutException()), null),
                logger);

            string? result = await monitor.TryReadInteractiveUiSearchAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Debug);
            _ = logger.Entries[0].Message.Should().Contain("read returned no response");
        }

        [Fact]
        public async Task TryReadInteractiveUiValuesAsync_ReturnsNull_AndDoesNotWarn_WhenResponseIsMissing()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ThrowingInteractiveUiStore(null, new InteractiveUiNoResponseException("missing", new TimeoutException())),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveUiValuesAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Debug);
            _ = logger.Entries[0].Message.Should().Be("Selection input read returned no response.");
        }

        [Fact]
        public async Task TryReadInteractiveUiSearchAsync_ReturnsNull_WhenWebSocketFails()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ThrowingInteractiveUiStore(new WebSocketException(), null),
                logger);

            string? result = await monitor.TryReadInteractiveUiSearchAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        }

        [Fact]
        public async Task TryReadInteractiveUiValuesAsync_ReturnsNull_WhenWebSocketFails()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ThrowingInteractiveUiStore(null, new WebSocketException()),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveUiValuesAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        }

        [Fact]
        public async Task TryReadInteractiveUiValuesAsync_ReturnsNull_WhenRangeIsZero()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ValueInteractiveUiStore(new SelectionInputValues(35.0f, 139.0f, 0f)),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveUiValuesAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().BeEmpty();
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(-10f)]
        public async Task TryReadInteractiveUiValuesAsync_ReturnsNull_WhenRangeIsInvalid(float rangeM)
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ValueInteractiveUiStore(new SelectionInputValues(35.0f, 139.0f, rangeM)),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveUiValuesAsync(CreateInputBinding(), CancellationToken.None);

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
        public async Task TryReadInteractiveUiValuesAsync_ReturnsNull_WhenCoordinatesAreInvalid(float latitude, float longitude)
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ValueInteractiveUiStore(new SelectionInputValues(latitude, longitude, 100f)),
                logger);

            SelectionInputValues? result = await monitor.TryReadInteractiveUiValuesAsync(CreateInputBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().BeEmpty();
        }

        [Fact]
        public async Task ReadAsync_FlagsInvalidValuesWithoutTreatingThemAsValidSelection()
        {
            var logger = new ListLogger<SelectionInputReader>();
            var monitor = new SelectionInputReader(
                new ValueInteractiveUiStore(new SelectionInputValues(35.0f, 139.0f, 0f)),
                logger);

            SelectionInputSnapshot snapshot = await monitor.ReadAsync(CreateInputBinding(), CancellationToken.None);

            _ = snapshot.Values.Should().BeNull();
            _ = snapshot.HasInvalidValues.Should().BeTrue();
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

        private static InteractiveUiBinding CreateInputBinding()
        {
            return new InteractiveUiBinding("binding");
        }

        private sealed class ThrowingInteractiveUiStore(Exception? searchException, Exception? valuesException) : IInteractiveUiStore
        {
            private readonly Exception? _searchException = searchException;
            private readonly Exception? _valuesException = valuesException;

            public Task<InteractiveUiBinding> CreateInteractiveUiBindingAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<SelectionInputValues?> ReadInteractiveUiValuesAsync(InteractiveUiBinding binding, CancellationToken cancellationToken)
            {
                return _valuesException is null
                    ? Task.FromResult<SelectionInputValues?>(null)
                    : Task.FromException<SelectionInputValues?>(_valuesException);
            }

            public Task<string?> ReadInteractiveUiSearchAsync(InteractiveUiBinding binding, CancellationToken cancellationToken)
            {
                return _searchException is null
                    ? Task.FromResult<string?>(null)
                    : Task.FromException<string?>(_searchException);
            }

            public Task UpdateInteractiveUiCoordinatesAsync(InteractiveUiBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class ValueInteractiveUiStore(SelectionInputValues values) : IInteractiveUiStore
        {
            public Task<InteractiveUiBinding> CreateInteractiveUiBindingAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<SelectionInputValues?> ReadInteractiveUiValuesAsync(InteractiveUiBinding binding, CancellationToken cancellationToken)
            {
                return Task.FromResult<SelectionInputValues?>(values);
            }

            public Task<string?> ReadInteractiveUiSearchAsync(InteractiveUiBinding binding, CancellationToken cancellationToken)
            {
                return Task.FromResult<string?>(null);
            }

            public Task UpdateInteractiveUiCoordinatesAsync(InteractiveUiBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
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
