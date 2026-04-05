namespace ThreeDTilesLink.Core.Models
{
    public sealed record InteractiveRunInput(
        IReadOnlyDictionary<string, RetainedTileState> RetainedTiles,
        bool RemoveOutOfRangeTiles,
        InteractiveRunCheckpoint? Checkpoint = null);
}
