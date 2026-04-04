namespace ThreeDTilesLink.Core.Models
{
    public sealed record ProbeConfiguration(
        string SlotName,
        string LatitudeVariablePath,
        string LongitudeVariablePath,
        string RangeVariablePath,
        double InitialLatitude,
        double InitialLongitude,
        double InitialRangeM);
}
