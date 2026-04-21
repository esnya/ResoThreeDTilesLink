using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed record TileSelectionRunExecutionResult(
        RunSummary Summary,
        IReadOnlyDictionary<string, RetainedTileState> VisibleTiles,
        IReadOnlyDictionary<string, RetainedTileState> CleanupDebtTiles,
        IReadOnlySet<string> SelectedTileStableIds,
        InteractiveRunCheckpoint Checkpoint);

    internal sealed class TileSelectionRunCounters
    {
        public int ProcessedTiles { get; set; }

        public int StreamedMeshes { get; set; }

        public int FailedTiles { get; set; }

        public long NextPreparedOrder { get; set; }
    }

    internal sealed record TileSelectionSendMeshResult(string? NodeId, Exception? Error);
}
