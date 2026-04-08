namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveTileRunResult(
        RunSummary Summary,
        IReadOnlyDictionary<string, RetainedTileState> VisibleTiles,
        IReadOnlySet<string> SelectedTileStableIds,
        InteractiveRunCheckpoint? Checkpoint,
        IReadOnlyDictionary<string, RetainedTileState>? CleanupDebtTiles = null)
    {
        public IReadOnlyDictionary<string, RetainedTileState> CleanupDebtTiles { get; init; } =
            CleanupDebtTiles ?? new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);
    }
}
