using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThreeDTilesLink.App;
using ThreeDTilesLink.Core.Runtime;

namespace ThreeDTilesLink
{
    internal static class TileStreamingRuntimeFactory
    {
        internal static Task<TileStreamingRuntime> CreateAsync(
            ILoggerFactory loggerFactory,
            IOptions<RuntimeOptions> runtimeOptions)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentNullException.ThrowIfNull(runtimeOptions);
            RuntimeOptions options = runtimeOptions.Value;
            return TileStreamingRuntime.CreateAsync(
                loggerFactory,
                TimeSpan.FromSeconds(options.TimeoutSec),
                options.ContentWorkers,
                options.ResoniteSendWorkers,
                options.MeasurePerformance);
        }
    }
}
