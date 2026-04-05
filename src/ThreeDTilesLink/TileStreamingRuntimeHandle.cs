using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Runtime;

namespace ThreeDTilesLink
{
    internal sealed class TileStreamingRuntimeHandle(
        ILoggerFactory loggerFactory,
        TileStreamingRuntime runtime) : IAsyncDisposable
    {
        private readonly ILoggerFactory _loggerFactory = loggerFactory;
        private bool _disposed;

        internal TileStreamingRuntime Runtime { get; } = runtime;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Runtime.DisposeAsync().ConfigureAwait(false);
            _loggerFactory.Dispose();
        }
    }
}
