using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TileSelectionInteractiveExecutionContext
    {
        private readonly Dictionary<string, RetainedTileState> _retainedTiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RetainedTileState> _cleanupDebtTiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _newSlotIds = new(StringComparer.Ordinal);

        public TileSelectionInteractiveExecutionContext(
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            IReadOnlyDictionary<string, RetainedTileState> cleanupDebtTiles,
            bool removeOutOfRangeTiles)
        {
            RemoveOutOfRangeTiles = removeOutOfRangeTiles;
            foreach ((string stableId, RetainedTileState retainedTile) in retainedTiles)
            {
                _retainedTiles[stableId] = retainedTile;
            }

            foreach ((string stableId, RetainedTileState retainedTile) in cleanupDebtTiles)
            {
                _cleanupDebtTiles[stableId] = retainedTile;
            }
        }

        public IReadOnlyDictionary<string, RetainedTileState> RetainedTiles => _retainedTiles;

        public IReadOnlyDictionary<string, RetainedTileState> CleanupDebtTiles => _cleanupDebtTiles;

        public bool RemoveOutOfRangeTiles { get; }

        public TileSelectionRunExecutionResult? LastState { get; private set; }

        public void UpdateState(TileSelectionRunExecutionResult state)
        {
            LastState = state;
        }

        public void TrackNewSlotId(string slotId)
        {
            if (!string.IsNullOrWhiteSpace(slotId))
            {
                _ = _newSlotIds.Add(slotId);
            }
        }

        public void ForgetNewSlotId(string slotId)
        {
            if (!string.IsNullOrWhiteSpace(slotId))
            {
                _ = _newSlotIds.Remove(slotId);
            }
        }

        public string[] GetNewSlotIds()
        {
            return [.. _newSlotIds];
        }
    }
}
