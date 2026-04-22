namespace ThreeDTilesLink.Core.Models
{
    internal sealed record LocationSearchResult(
        string FormattedAddress,
        double Latitude,
        double Longitude);
}
