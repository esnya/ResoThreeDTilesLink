using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace ThreeDTilesLink.App
{
    internal sealed class RuntimeOptions
    {
        [Range(1, int.MaxValue)]
        public int ContentWorkers { get; set; }

        [Range(1, int.MaxValue)]
        public int ResoniteSendWorkers { get; set; }

        [Range(1, int.MaxValue)]
        public int TimeoutSec { get; set; }

        public bool MeasurePerformance { get; set; }

        [Required]
        public LogLevel LogLevel { get; set; }
    }
}
