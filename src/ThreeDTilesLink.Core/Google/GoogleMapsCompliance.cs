namespace ThreeDTilesLink.Core.Google
{
    internal static class GoogleMapsCompliance
    {
        internal const string BasemapAttribution = "Google Maps";
        internal const string BundledLogoFileName = "GoogleMaps_Logo_WithLightOutline_2x.png";
        internal const string AttributionRequirements =
            "Keep Google Maps attribution visible for displayed tiles. If your renderer can show a logo, use the official Google Maps logo, keep it separate from renderer or overlay logos, and keep third-party data providers in the attribution line.";

        internal static string? NormalizeAttributionOwner(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string normalized = value.Trim();
            return string.Equals(normalized, "Google", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, BasemapAttribution, StringComparison.OrdinalIgnoreCase)
                ? BasemapAttribution
                : normalized;
        }
    }
}
