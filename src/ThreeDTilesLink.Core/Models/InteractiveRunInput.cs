namespace ThreeDTilesLink.Core.Models
{
    internal sealed record InteractiveRunInput(
        IReadOnlyDictionary<string, RetainedTileState> RetainedTiles,
        bool RemoveOutOfRangeTiles,
        InteractiveRunCheckpoint? Checkpoint = null,
        IReadOnlyDictionary<string, RetainedTileState>? CleanupDebtTiles = null)
    {
        public IReadOnlyDictionary<string, RetainedTileState> CleanupDebtTiles { get; init; } =
            CleanupDebtTiles ?? new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);
    }
}
