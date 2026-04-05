namespace ThreeDTilesLink.Core.Models
{
    internal sealed record WatchConfiguration(
        string LatitudeVariablePath,
        string LongitudeVariablePath,
        string RangeVariablePath,
        string SearchVariablePath);
}
