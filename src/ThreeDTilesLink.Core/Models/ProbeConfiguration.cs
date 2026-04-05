namespace ThreeDTilesLink.Core.Models
{
    internal sealed record ProbeConfiguration(
        string SlotName,
        string LatitudeVariablePath,
        string LongitudeVariablePath,
        string RangeVariablePath,
        string SearchVariablePath);
}
