namespace ThreeDTilesLink.Core.Models
{
    public abstract record SchedulerWorkItem(SchedulerWorkKind Kind);

    public sealed record FetchNestedTilesetWorkItem(TileSelectionResult Tile)
        : SchedulerWorkItem(SchedulerWorkKind.FetchNestedTileset);

    public sealed record StreamGlbTileWorkItem(TileSelectionResult Tile)
        : SchedulerWorkItem(SchedulerWorkKind.StreamGlbTile);

    public sealed record RemoveParentTileSlotsWorkItem(string StateId, string TileId, IReadOnlyList<string> SlotIds)
        : SchedulerWorkItem(SchedulerWorkKind.RemoveParentTileSlots);

    public sealed record UpdateLicenseCreditWorkItem(string CreditString)
        : SchedulerWorkItem(SchedulerWorkKind.UpdateLicenseCredit);
}
