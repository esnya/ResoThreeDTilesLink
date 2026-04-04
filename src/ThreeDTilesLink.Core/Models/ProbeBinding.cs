namespace ThreeDTilesLink.Core.Models
{
    public sealed record ProbeBinding(
        string SlotId,
        bool OwnsSlot,
        string LatitudeComponentId,
        string LatitudeValueMemberName,
        string LongitudeComponentId,
        string LongitudeValueMemberName,
        string RangeComponentId,
        string RangeValueMemberName);
}
