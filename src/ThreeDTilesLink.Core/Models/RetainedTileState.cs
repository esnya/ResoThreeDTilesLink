namespace ThreeDTilesLink.Core.Models
{
    public sealed record RetainedTileState(
        string StableId,
        string TileId,
        string? ParentStableId,
        IReadOnlyList<string> AncestorStableIds,
        IReadOnlyList<string> SlotIds,
        string? AssetCopyright);
}
