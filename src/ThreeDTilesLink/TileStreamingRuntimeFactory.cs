using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Runtime;

namespace ThreeDTilesLink
{
    internal static class TileStreamingRuntimeFactory
    {
        internal static TileStreamingRuntimeHandle Create(
            LogLevel logLevel,
            TimeSpan requestTimeout,
            int maxConcurrentTileProcessing = 8)
        {
#pragma warning disable CA2000
            ILoggerFactory loggerFactory = CreateLoggerFactory(logLevel);
            try
            {
                return new TileStreamingRuntimeHandle(
                    loggerFactory,
                    new TileStreamingRuntime(
                        loggerFactory,
                        requestTimeout,
                        maxConcurrentTileProcessing));
            }
            catch
            {
                loggerFactory.Dispose();
                throw;
            }
#pragma warning restore CA2000
        }

        private static ILoggerFactory CreateLoggerFactory(LogLevel logLevel)
        {
            return LoggerFactory.Create(builder =>
            {
                _ = builder
                    .SetMinimumLevel(logLevel)
                    .AddSimpleConsole(options =>
                    {
                        options.IncludeScopes = false;
                        options.SingleLine = true;
                        options.TimestampFormat = "HH:mm:ss ";
                    });
            });
        }
    }
}
