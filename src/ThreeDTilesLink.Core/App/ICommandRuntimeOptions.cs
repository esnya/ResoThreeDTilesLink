using Microsoft.Extensions.Logging;

namespace ThreeDTilesLink.Core.App
{
    internal interface ICommandRuntimeOptions
    {
        int ContentWorkers { get; }
        int ResoniteSendWorkers { get; }
        int TimeoutSec { get; }
        bool MeasurePerformance { get; }
        LogLevel LogLevel { get; }
    }
}
