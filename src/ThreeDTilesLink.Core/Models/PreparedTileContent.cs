namespace ThreeDTilesLink.Core.Models
{
    internal sealed record PreparedTileContent(
        TileSelectionResult Tile,
        IReadOnlyList<PlacedMeshPayload> Meshes,
        string? AssetCopyright);
}
