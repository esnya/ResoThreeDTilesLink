namespace ThreeDTilesLink.Core.Models
{
    public sealed record PreparedTileContent(
        TileSelectionResult Tile,
        IReadOnlyList<PlacedMeshPayload> Meshes,
        string? AssetCopyright);
}
