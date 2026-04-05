namespace ThreeDTilesLink.Core.Models
{
    internal sealed record RunSummary(
        int CandidateTiles,
        int ProcessedTiles,
        int StreamedMeshes,
        int FailedTiles);
}
