using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class PlannerState
    {
        internal TileRunRequest? Request { get; set; }
        internal QueryRange? Range { get; set; }
        internal bool Initialized { get; set; }
        internal bool Stopped { get; set; }
        internal bool BootstrapActive { get; set; }
        internal double RenderStartSpanM { get; set; }
        internal int MaxNestedTilesetFetches { get; set; }
        internal int NestedTilesetFetches { get; set; }
        internal int StreamedMeshes { get; set; }
        internal int FailedTiles { get; set; }
        internal int ProcessedTiles { get; set; }
        internal int CandidateTiles { get; set; }
        internal int StreamedTileCount { get; set; }
        internal Dictionary<string, Tileset> TilesetCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, TileLifecycle> TileStates { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> QueuedGlbTileIds { get; } = new(StringComparer.Ordinal);
        internal PriorityQueue<PendingTileset, double> PendingTilesets { get; } = new();
        internal PriorityQueue<TileSelectionResult, double> PendingGlbTiles { get; } = new();
        internal Dictionary<string, TileSelectionResult> DeferredGlbTiles { get; } = new(StringComparer.Ordinal);
        internal Queue<PlannerCommand> Outbound { get; } = new();

        internal sealed record PendingTileset(
            Tileset Tileset,
            Math.Matrix4x4d ParentWorld,
            string IdPrefix,
            int DepthOffset,
            string? OwnerTileId,
            string? OwnerStableId);

        internal sealed class TileLifecycle(string stateId, string tileId)
        {
            public string StateId { get; } = stateId;
            public string TileId { get; } = tileId;
            public string? ParentStateId { get; set; }
            public TileContentKind ContentKind { get; set; } = TileContentKind.Other;
            public bool SelfCompleted { get; set; }
            public bool BranchCompleted { get; set; }
            public bool ChildrenDiscoveryDone { get; set; } = true;
            public bool Removed { get; set; }
            public bool RemovalQueued { get; set; }
            public bool DeferredSuppressed { get; set; }
            public bool FallbackQueued { get; set; }
            public bool BranchHasVisibleContent { get; set; }
            public IReadOnlyList<string> AttributionOwners { get; set; } = [];
            public bool AttributionsApplied { get; set; }
            public HashSet<string> DirectChildren { get; } = new(StringComparer.Ordinal);
            public HashSet<string> PendingChildBranches { get; } = new(StringComparer.Ordinal);
            public HashSet<string> SlotIds { get; } = new(StringComparer.Ordinal);
        }
    }
}
