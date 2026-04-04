namespace ThreeDTilesLink.Core.Models
{
    public sealed record InteractiveTileRunResult(
        RunSummary Summary,
        IReadOnlyDictionary<string, RetainedTileState> VisibleTiles,
        IReadOnlySet<string> SelectedTileStableIds,
        InteractiveRunCheckpoint? Checkpoint);
}
