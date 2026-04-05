namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveRunInput(
        IReadOnlyDictionary<string, RetainedTileState> RetainedTiles,
        bool RemoveOutOfRangeTiles,
        InteractiveRunCheckpoint? Checkpoint = null);
}
