namespace ThreeDTilesLink.Core.Models
{
    internal sealed record WatchBinding(
        string SlotId,
        bool OwnsSlot,
        string LatitudeComponentId,
        string LatitudeValueMemberName,
        string LatitudeAliasComponentId,
        string LatitudeAliasValueMemberName,
        string LongitudeComponentId,
        string LongitudeValueMemberName,
        string LongitudeAliasComponentId,
        string LongitudeAliasValueMemberName,
        string RangeComponentId,
        string RangeValueMemberName,
        string RangeAliasComponentId,
        string RangeAliasValueMemberName,
        string SearchComponentId,
        string SearchValueMemberName,
        string SearchAliasComponentId,
        string SearchAliasValueMemberName);
}
