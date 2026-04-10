using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;

namespace ThreeDTilesLink.App
{
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The options binder creates this type through generic host configuration binding.")]
    internal sealed class GoogleMapsOptions
    {
        [Required(AllowEmptyStrings = false)]
        public string ApiKey { get; set; } = string.Empty;
    }
}
