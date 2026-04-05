using Microsoft.Extensions.Logging;

namespace ThreeDTilesLink.Core.App
{
    internal interface ICommandRuntimeOptions
    {
        int ContentWorkers { get; }
        int TimeoutSec { get; }
        LogLevel LogLevel { get; }
    }
}
