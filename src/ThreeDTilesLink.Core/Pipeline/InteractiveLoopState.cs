using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed record InteractiveActiveRun(
        Task<InteractiveTileRunResult> Task,
        CancellationTokenSource CancellationSource);

    internal sealed record InteractiveRangeFootprint(
        GeoReference Reference,
        double RangeM);

    internal sealed record InteractiveLoopState(
        GeoReference? PlacementReference,
        InteractiveRangeFootprint? LastRequestedFootprint,
        InteractiveActiveRun? ActiveRun,
        Dictionary<string, RetainedTileState> RetainedTiles,
        Dictionary<string, RetainedTileState> CleanupDebtTiles,
        InteractiveRunCheckpoint? Checkpoint,
        InteractiveUiBinding? InputBinding,
        SelectionInputValues? LastObservedValues,
        SelectionInputValues? PendingValues,
        DateTimeOffset? PendingValuesChangedAt,
        string? LastObservedSearch,
        string? LastResolvedSearch,
        string? PendingSearch,
        DateTimeOffset? PendingSearchChangedAt,
        LocationSearchResult? AwaitingResolvedCoordinates,
        DateTimeOffset LastRunStartedAt,
        bool Connected)
    {
        internal static InteractiveLoopState CreateInitial()
        {
            return new InteractiveLoopState(
                PlacementReference: null,
                LastRequestedFootprint: null,
                ActiveRun: null,
                RetainedTiles: new Dictionary<string, RetainedTileState>(StringComparer.Ordinal),
                CleanupDebtTiles: new Dictionary<string, RetainedTileState>(StringComparer.Ordinal),
                Checkpoint: null,
                InputBinding: null,
                LastObservedValues: null,
                PendingValues: null,
                PendingValuesChangedAt: null,
                LastObservedSearch: null,
                LastResolvedSearch: null,
                PendingSearch: null,
                PendingSearchChangedAt: null,
                AwaitingResolvedCoordinates: null,
                LastRunStartedAt: DateTimeOffset.MinValue,
                Connected: false);
        }
    }
}
