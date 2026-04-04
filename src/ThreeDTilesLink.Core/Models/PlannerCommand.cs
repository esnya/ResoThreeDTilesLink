namespace ThreeDTilesLink.Core.Models
{
    public enum PlannerCommandKind
    {
        ProcessTileContent = 0,
        StreamPlacedMeshes = 1,
        RemoveSlots = 2,
        UpdateLicenseCredit = 3
    }

    public abstract record PlannerCommand(PlannerCommandKind Kind);

    public sealed record ProcessTileContentCommand(TileSelectionResult Tile)
        : PlannerCommand(PlannerCommandKind.ProcessTileContent);

    public sealed record StreamPlacedMeshesCommand(
        TileSelectionResult Tile,
        IReadOnlyList<PlacedMeshPayload> Meshes,
        string? AssetCopyright)
        : PlannerCommand(PlannerCommandKind.StreamPlacedMeshes);

    public sealed record RemoveSlotsCommand(
        string StateId,
        string TileId,
        IReadOnlyList<string> SlotIds)
        : PlannerCommand(PlannerCommandKind.RemoveSlots);

    public sealed record UpdateLicenseCreditCommand(string CreditString)
        : PlannerCommand(PlannerCommandKind.UpdateLicenseCredit);
}
