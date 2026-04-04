using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Models
{
    public abstract record PlannerResult(PlannerCommandKind Kind);

    public sealed record NestedTilesetLoadedResult(
        TileSelectionResult Tile,
        Tileset Tileset)
        : PlannerResult(PlannerCommandKind.ProcessTileContent);

    public sealed record RenderableContentReadyResult(
        TileSelectionResult Tile,
        int StreamedMeshCount,
        IReadOnlyList<string> SlotIds,
        string? AssetCopyright)
        : PlannerResult(PlannerCommandKind.StreamPlacedMeshes);

    public sealed record ContentSkippedResult(
        TileSelectionResult Tile,
        string? Reason = null,
        Exception? Error = null)
        : PlannerResult(PlannerCommandKind.ProcessTileContent);

    public sealed record ContentFailedResult(
        TileSelectionResult Tile,
        Exception Error,
        int StreamedMeshCount = 0,
        IReadOnlyList<string>? SlotIds = null,
        string? AssetCopyright = null)
        : PlannerResult(PlannerCommandKind.ProcessTileContent);

    public sealed record SlotsRemovedResult(
        string StateId,
        string TileId,
        bool Succeeded,
        int FailedSlotCount,
        Exception? Error)
        : PlannerResult(PlannerCommandKind.RemoveSlots);

    public sealed record LicenseUpdatedResult(
        string CreditString,
        bool Succeeded,
        Exception? Error)
        : PlannerResult(PlannerCommandKind.UpdateLicenseCredit);
}
