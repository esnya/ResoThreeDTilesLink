using System.ComponentModel.DataAnnotations;

namespace ThreeDTilesLink.App
{
    internal sealed class GoogleMapsOptions
    {
        [Required(AllowEmptyStrings = false)]
        public string ApiKey { get; set; } = string.Empty;
    }
}
