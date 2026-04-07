using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal enum ContentDiscoveryStatus
    {
        Unrequested = 0,
        InFlight = 1,
        Ready = 2,
        Skipped = 3,
        Failed = 4
    }

    internal sealed class TileBranchFact(TileSelectionResult tile)
    {
        private const int MaxCompleteSendRetries = 1;

        public TileSelectionResult Tile { get; } = tile;

        public ContentDiscoveryStatus PrepareStatus { get; set; } = ContentDiscoveryStatus.Unrequested;

        public ContentDiscoveryStatus NestedStatus { get; set; } = tile.ContentKind == TileContentKind.Json
            ? ContentDiscoveryStatus.Unrequested
            : ContentDiscoveryStatus.Skipped;

        public PreparedTileContent? PreparedContent { get; set; }

        public long PreparedOrder { get; set; } = long.MaxValue;

        public bool NestedExpanded { get; set; }

        public bool SendFailed { get; set; }

        public string? AssetCopyright { get; set; }

        public int CompleteSendFailureCount { get; set; }

        public bool HasRenderable => Tile.ContentKind == TileContentKind.Glb;

        public bool CanRetryCompleteSendFailure => CompleteSendFailureCount <= MaxCompleteSendRetries;
    }

    internal sealed class DiscoveryFacts(
        TileRunRequest request,
        IReadOnlyDictionary<string, Tileset>? cachedTilesets,
        bool removeOutOfRangeRetainedTiles)
    {
        public TileRunRequest Request { get; } = request;

        public QueryRange Range { get; } = new(request.Traversal.RangeM);

        public bool RemoveOutOfRangeRetainedTiles { get; } = removeOutOfRangeRetainedTiles;

        public Dictionary<string, TileBranchFact> Branches { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Tileset> TilesetCache { get; } = cachedTilesets is null
            ? new Dictionary<string, Tileset>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Tileset>(cachedTilesets, StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class WriterState(IReadOnlyDictionary<string, RetainedTileState>? initialVisibleTiles = null)
    {
        public Dictionary<string, RetainedTileState> VisibleTiles { get; } = initialVisibleTiles is null
            ? new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            : new Dictionary<string, RetainedTileState>(initialVisibleTiles, StringComparer.Ordinal);

        public Dictionary<string, DateTimeOffset> VisibleSinceByStableId { get; } = initialVisibleTiles is null
            ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            : initialVisibleTiles.Keys.ToDictionary(
                static stableId => stableId,
                static _ => DateTimeOffset.MinValue,
                StringComparer.Ordinal);

        public HashSet<string> FailedRemovalStableIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> InFlightSendStableIds { get; } = new(StringComparer.Ordinal);

        public string? InFlightRemoveStableId { get; set; }

        public bool MetadataInFlight { get; set; }

        public string AppliedLicenseCredit { get; set; } = string.Empty;

        public float AppliedProgressValue { get; set; } = -1f;

        public string AppliedProgressText { get; set; } = string.Empty;
    }

    internal sealed record DesiredView(
        IReadOnlySet<string> StableIds,
        IReadOnlySet<string> SelectedStableIds,
        IReadOnlySet<string> CandidateStableIds);

    internal abstract record DiscoveryWorkItem(TileSelectionResult Tile);

    internal sealed record LoadNestedTilesetWorkItem(TileSelectionResult Tile)
        : DiscoveryWorkItem(Tile);

    internal sealed record PrepareTileWorkItem(TileSelectionResult Tile)
        : DiscoveryWorkItem(Tile);

    internal sealed record DiscoveryTaskEntry(
        DiscoveryWorkItem Work,
        Task<DiscoveryCompletion> Task);

    internal abstract record DiscoveryCompletion(TileSelectionResult Tile);

    internal sealed record NestedTilesetDiscovered(TileSelectionResult Tile, Tileset Tileset)
        : DiscoveryCompletion(Tile);

    internal sealed record TilePrepared(TileSelectionResult Tile, PreparedTileContent Content)
        : DiscoveryCompletion(Tile);

    internal sealed record DiscoverySkipped(TileSelectionResult Tile, string? Reason = null, Exception? Error = null)
        : DiscoveryCompletion(Tile);

    internal sealed record DiscoveryFailed(TileSelectionResult Tile, Exception Error)
        : DiscoveryCompletion(Tile);

    internal abstract record WriterCommand;

    internal sealed record SendTileWriterCommand(PreparedTileContent Content)
        : WriterCommand;

    internal sealed record RemoveTileWriterCommand(string StableId, string TileId, IReadOnlyList<string> SlotIds)
        : WriterCommand;

    internal sealed record DelayWriterCommand(TimeSpan Delay)
        : WriterCommand;

    internal sealed record SyncSessionMetadataWriterCommand(
        string LicenseCredit,
        float ProgressValue,
        string ProgressText)
        : WriterCommand;

    internal abstract record WriterCompletion;

    internal sealed record SendTileCompleted(
        PreparedTileContent Content,
        bool Succeeded,
        int StreamedMeshCount,
        IReadOnlyList<string> SlotIds,
        Exception? Error = null)
        : WriterCompletion;

    internal sealed record RemoveTileCompleted(
        string StableId,
        string TileId,
        bool Succeeded,
        int FailedSlotCount,
        IReadOnlyList<string> RemainingSlotIds,
        Exception? Error = null)
        : WriterCompletion;

    internal sealed record DelayCompleted(TimeSpan Delay)
        : WriterCompletion;

    internal sealed record SyncSessionMetadataCompleted(
        string LicenseCredit,
        float ProgressValue,
        string ProgressText,
        bool Succeeded,
        Exception? Error = null)
        : WriterCompletion;

    internal sealed record ProgressSnapshot(
        int CandidateTiles,
        int ProcessedTiles,
        int StreamedMeshes,
        int FailedTiles);

    internal sealed class PlanningNode(TileBranchFact fact)
    {
        public TileBranchFact Fact { get; } = fact;

        public string StableId => Fact.Tile.StableId!;

        public PlanningNode? Parent { get; set; }

        public List<PlanningNode> Children { get; } = [];
    }

    internal sealed class PlanningTree(
        TileRunRequest request,
        IReadOnlyDictionary<string, PlanningNode> nodes,
        IReadOnlyList<PlanningNode> roots,
        IReadOnlyList<RetainedTileState> planningVisibleTiles,
        IReadOnlySet<string> selectedStableIds,
        IReadOnlySet<string> planningVisibleStableIds,
        IReadOnlySet<string> ancestorsWithPlanningVisibleDescendants,
        IReadOnlySet<string> ancestorsWithOutOfSelectionVisibleDescendants)
    {
        public TileRunRequest Request { get; } = request;

        public IReadOnlyDictionary<string, PlanningNode> Nodes { get; } = nodes;

        public IReadOnlyList<PlanningNode> Roots { get; } = roots;

        public IReadOnlyList<RetainedTileState> PlanningVisibleTiles { get; } = planningVisibleTiles;

        public IReadOnlySet<string> SelectedStableIds { get; } = selectedStableIds;

        public IReadOnlySet<string> PlanningVisibleStableIds { get; } = planningVisibleStableIds;

        public IReadOnlySet<string> AncestorsWithPlanningVisibleDescendants { get; } = ancestorsWithPlanningVisibleDescendants;

        public IReadOnlySet<string> AncestorsWithOutOfSelectionVisibleDescendants { get; } = ancestorsWithOutOfSelectionVisibleDescendants;
    }
}
