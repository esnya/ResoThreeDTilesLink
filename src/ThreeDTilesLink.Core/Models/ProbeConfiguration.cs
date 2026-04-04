namespace ThreeDTilesLink.Core.Models
{
    public sealed record ProbeConfiguration(
        string SlotName,
        string LatitudeVariablePath,
        string LongitudeVariablePath,
        string RangeVariablePath,
        string SearchVariablePath);
}
