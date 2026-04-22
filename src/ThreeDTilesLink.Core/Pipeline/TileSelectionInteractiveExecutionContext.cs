using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TileSelectionInteractiveExecutionContext
    {
        private readonly Dictionary<string, RetainedTileState> _retainedTiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RetainedTileState> _cleanupDebtTiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _newNodeIds = new(StringComparer.Ordinal);

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

        public void TrackNewNodeId(string nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                _ = _newNodeIds.Add(nodeId);
            }
        }

        public void ForgetNewNodeId(string nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                _ = _newNodeIds.Remove(nodeId);
            }
        }

        public string[] GetNewNodeIds()
        {
            return [.. _newNodeIds];
        }
    }
}
