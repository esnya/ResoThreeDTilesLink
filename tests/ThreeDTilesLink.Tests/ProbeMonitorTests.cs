using FluentAssertions;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Tests
{
    public sealed class ProbeMonitorTests
    {
        [Fact]
        public async Task TryReadProbeSearchAsync_ReturnsNull_AndDoesNotWarn_WhenResponseIsMissing()
        {
            var logger = new ListLogger<ProbeMonitor>();
            var monitor = new ProbeMonitor(
                new ThrowingProbeStore(new ResoniteLinkNoResponseException(), null),
                logger);

            string? result = await monitor.TryReadProbeSearchAsync(CreateBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Debug);
            _ = logger.Entries[0].Message.Should().Be("Probe search query read returned no response.");
        }

        [Fact]
        public async Task TryReadProbeValuesAsync_ReturnsNull_AndDoesNotWarn_WhenResponseIsMissing()
        {
            var logger = new ListLogger<ProbeMonitor>();
            var monitor = new ProbeMonitor(
                new ThrowingProbeStore(null, new ResoniteLinkNoResponseException()),
                logger);

            ProbeValues? result = await monitor.TryReadProbeValuesAsync(CreateBinding(), CancellationToken.None);

            _ = result.Should().BeNull();
            _ = logger.Entries.Should().ContainSingle();
            _ = logger.Entries[0].Level.Should().Be(LogLevel.Debug);
            _ = logger.Entries[0].Message.Should().Be("Probe values read returned no response.");
        }

        private static ProbeBinding CreateBinding()
        {
            return new ProbeBinding(
                "probe",
                false,
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

        private sealed class ThrowingProbeStore(Exception? searchException, Exception? valuesException) : IProbeStore
        {
            private readonly Exception? _searchException = searchException;
            private readonly Exception? _valuesException = valuesException;

            public Task<ProbeBinding> CreateProbeAsync(ProbeConfiguration configuration, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<ProbeValues?> ReadProbeValuesAsync(ProbeBinding binding, CancellationToken cancellationToken)
            {
                return _valuesException is null
                    ? Task.FromResult<ProbeValues?>(null)
                    : Task.FromException<ProbeValues?>(_valuesException);
            }

            public Task<string?> ReadProbeSearchAsync(ProbeBinding binding, CancellationToken cancellationToken)
            {
                return _searchException is null
                    ? Task.FromResult<string?>(null)
                    : Task.FromException<string?>(_searchException);
            }

            public Task UpdateProbeCoordinatesAsync(ProbeBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
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
