using ThreeDTilesLink.Core.Contracts;

namespace ThreeDTilesLink.Core.Generic
{
    internal sealed class GenericTileLicenseCreditPolicy : ILicenseCreditPolicy
    {
        public string DefaultCredit => string.Empty;

        public string AttributionRequirements => string.Empty;

        public string? NormalizeOwner(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }
    }
}
