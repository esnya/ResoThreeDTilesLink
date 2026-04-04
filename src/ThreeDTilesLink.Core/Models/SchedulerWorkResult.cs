using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    public abstract record SchedulerWorkResult(SchedulerWorkKind Kind);

    public sealed record FetchNestedTilesetWorkResult(
        TileSelectionResult Tile,
        bool Succeeded,
        Tileset? Tileset,
        bool IsBadRequest,
        Exception? Error)
        : SchedulerWorkResult(SchedulerWorkKind.FetchNestedTileset);

    public enum StreamGlbOutcome
    {
        Success = 0,
        BadRequest = 1,
        Failed = 2
    }

    public sealed record StreamGlbTileWorkResult(
        TileSelectionResult Tile,
        StreamGlbOutcome Outcome,
        int StreamedMeshCount,
        IReadOnlyList<string> SlotIds,
        string? AssetCopyright,
        Exception? Error)
        : SchedulerWorkResult(SchedulerWorkKind.StreamGlbTile);

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
