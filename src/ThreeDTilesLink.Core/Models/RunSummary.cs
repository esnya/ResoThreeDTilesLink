namespace ThreeDTilesLink.Core.Models
{
    public sealed record RunSummary(
        int CandidateTiles,
        int ProcessedTiles,
        int StreamedMeshes,
        int FailedTiles);
}
