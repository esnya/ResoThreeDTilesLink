using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace ThreeDTilesLink.App
{
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The options binder creates this type through generic host configuration binding.")]
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
