namespace ThreeDTilesLink.Core.Models
{
    public abstract record SchedulerWorkItem(SchedulerWorkKind Kind);

    public sealed record ProcessNodeContentWorkItem(TileSelectionResult Tile)
        : SchedulerWorkItem(SchedulerWorkKind.ProcessNodeContent);

    public sealed record RemoveParentTileSlotsWorkItem(string StateId, string TileId, IReadOnlyList<string> SlotIds)
        : SchedulerWorkItem(SchedulerWorkKind.RemoveParentTileSlots);

    public sealed record UpdateLicenseCreditWorkItem(string CreditString)
        : SchedulerWorkItem(SchedulerWorkKind.UpdateLicenseCredit);
}
