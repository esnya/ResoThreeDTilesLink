namespace ThreeDTilesLink.Core.Models
{
    public sealed record RetainedTileState(
        string StableId,
        string TileId,
        IReadOnlyList<string> SlotIds,
        string? AssetCopyright);
}
