using ThreeDTilesLink.Core.Contracts;

namespace ThreeDTilesLink.Core.Google
{
    internal sealed class GoogleTileLicenseCreditPolicy : ILicenseCreditPolicy
    {
        public string DefaultCredit => GoogleMapsCompliance.BasemapAttribution;

        public string AttributionRequirements => GoogleMapsCompliance.AttributionRequirements;

        public string? NormalizeOwner(string? value) => GoogleMapsCompliance.NormalizeAttributionOwner(value);
    }
}
