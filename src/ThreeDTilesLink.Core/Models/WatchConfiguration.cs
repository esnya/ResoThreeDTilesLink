namespace ThreeDTilesLink.Core.Models
{
    internal sealed record WatchConfiguration(
        string SlotName,
        string LatitudeVariablePath,
        string LongitudeVariablePath,
        string RangeVariablePath,
        string SearchVariablePath);
}
