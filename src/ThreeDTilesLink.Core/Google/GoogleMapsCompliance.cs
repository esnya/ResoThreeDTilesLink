namespace ThreeDTilesLink.Core.Google
{
    internal static class GoogleMapsCompliance
    {
        internal const string BasemapAttribution = "Google Maps";
        internal const string AttributionRequirements =
            "Keep Google Maps attribution visible for displayed tiles. If your renderer requires a Google Maps logo for compliance, handle that logo in the renderer or overlay layer, keep it separate from renderer branding, and keep third-party data providers in the attribution line.";

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
