namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveTileRunResult(
        RunSummary Summary,
        IReadOnlyDictionary<string, RetainedTileState> VisibleTiles,
        IReadOnlySet<string> SelectedTileStableIds,
        InteractiveRunCheckpoint? Checkpoint);
}
