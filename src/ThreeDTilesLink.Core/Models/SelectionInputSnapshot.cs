namespace ThreeDTilesLink.Core.Models
{
    internal sealed record SelectionInputSnapshot(
        string? SearchText,
        SelectionInputValues? Values,
        bool HasInvalidValues = false);
}
