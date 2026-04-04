namespace ThreeDTilesLink.Core.Models
{
    public sealed record ProbeBinding(
        string SlotId,
        string LatitudeComponentId,
        string LatitudeValueMemberName,
        string LongitudeComponentId,
        string LongitudeValueMemberName,
        string RangeComponentId,
        string RangeValueMemberName);
}
