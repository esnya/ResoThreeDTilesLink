namespace ThreeDTilesLink.Core.Models
{
    public sealed record PlannerProgress(
        int CandidateTiles,
        int ProcessedTiles,
        int StreamedMeshes,
        int FailedTiles,
        int PendingTilesets,
        int PendingGlbTiles,
        int DeferredGlbTiles,
        int PendingTileCommands,
        int PendingPreparedStreams = 0);
}
