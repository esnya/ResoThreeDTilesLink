using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    public abstract record SchedulerWorkResult(SchedulerWorkKind Kind);

    public sealed record ProcessNodeContentWorkResult(
        TileSelectionResult Tile,
        ProcessNodeContentOutcome Outcome)
        : SchedulerWorkResult(SchedulerWorkKind.ProcessNodeContent);

    public abstract record ProcessNodeContentOutcome;

    public sealed record NestedTilesetContentOutcome(Tileset Tileset)
        : ProcessNodeContentOutcome;

    public sealed record StreamedRenderableContentOutcome(
        int StreamedMeshCount,
        IReadOnlyList<string> SlotIds,
        string? AssetCopyright)
        : ProcessNodeContentOutcome;

    public sealed record UnavailableContentOutcome(Exception? Error = null)
        : ProcessNodeContentOutcome;

    public sealed record UnsupportedContentOutcome(string? Reason = null)
        : ProcessNodeContentOutcome;

    public sealed record FailedContentOutcome(
        Exception Error,
        int StreamedMeshCount = 0,
        IReadOnlyList<string>? SlotIds = null,
        string? AssetCopyright = null)
        : ProcessNodeContentOutcome;

    public sealed record RemoveParentTileSlotsWorkResult(
        string StateId,
        string TileId,
        bool Succeeded,
        int FailedSlotCount,
        Exception? Error)
        : SchedulerWorkResult(SchedulerWorkKind.RemoveParentTileSlots);

    public sealed record UpdateLicenseCreditWorkResult(
        string CreditString,
        bool Succeeded,
        Exception? Error)
        : SchedulerWorkResult(SchedulerWorkKind.UpdateLicenseCredit);
}
