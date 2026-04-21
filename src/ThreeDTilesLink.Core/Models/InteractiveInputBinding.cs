namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveInputBinding(
        string LatitudeComponentId,
        string LatitudeAliasComponentId,
        string LongitudeComponentId,
        string LongitudeAliasComponentId,
        string RangeComponentId,
        string RangeAliasComponentId,
        string SearchComponentId,
        string SearchAliasComponentId);
}
