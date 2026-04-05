namespace ThreeDTilesLink.Core.Models
{
    internal sealed record RetainedTileState(
        string StableId,
        string TileId,
        string? ParentStableId,
        IReadOnlyList<string> AncestorStableIds,
        IReadOnlyList<string> SlotIds,
        string? AssetCopyright);
}
