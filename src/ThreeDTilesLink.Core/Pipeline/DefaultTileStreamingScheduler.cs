using System.Globalization;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class DefaultTileStreamingScheduler(
        ITileSelector selector,
        ILogger<DefaultTileStreamingScheduler> logger) : ITileStreamingScheduler
    {
        private readonly ITileSelector _selector = selector;
        private readonly ILogger<DefaultTileStreamingScheduler> _logger = logger;

        private StreamerOptions? _options;
        private QueryRange? _range;
        private bool _initialized;
        private bool _stopped;
        private bool _bootstrapActive;
        private double _renderStartSpanM;
        private int _maxNestedTilesetFetches;
        private int _nestedTilesetFetches;

        private int _streamedMeshes;
        private int _failedTiles;
        private int _processedTiles;
        private int _candidateTiles;
        private int _streamedTileCount;

        private readonly List<string> _attributionOrder = [];
        private readonly HashSet<string> _knownAttributions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _activeAttributionCounts = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Tileset> _tilesetCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TileLifecycle> _tileStates = new(StringComparer.Ordinal);
        private readonly HashSet<string> _queuedGlbTileIds = new(StringComparer.Ordinal);
        private readonly PriorityQueue<PendingTileset, double> _pendingTilesets = new();
        private readonly PriorityQueue<TileSelectionResult, double> _pendingGlbTiles = new();
        private readonly Dictionary<string, TileSelectionResult> _deferredGlbTiles = new(StringComparer.Ordinal);
        private readonly Queue<SchedulerWorkItem> _outbound = new();

        private StreamerOptions Options => _options ?? throw new InvalidOperationException("Scheduler is not initialized.");
        private QueryRange Range => _range ?? throw new InvalidOperationException("Scheduler is not initialized.");

        public void Initialize(Tileset rootTileset, StreamerOptions options)
        {
            ArgumentNullException.ThrowIfNull(rootTileset);
            ArgumentNullException.ThrowIfNull(options);

            _options = options;
            _range = new QueryRange(options.RangeM);
            _initialized = true;
            _stopped = false;

            _bootstrapActive = true;
            _renderStartSpanM = options.RangeM * (options.BootstrapRangeMultiplier > 0d ? options.BootstrapRangeMultiplier : 4d);
            _maxNestedTilesetFetches = SMath.Max(options.MaxTiles * 64, 512);
            _nestedTilesetFetches = 0;

            _streamedMeshes = 0;
            _failedTiles = 0;
            _processedTiles = 0;
            _candidateTiles = 0;
            _streamedTileCount = 0;

            _attributionOrder.Clear();
            _knownAttributions.Clear();
            _activeAttributionCounts.Clear();
            _tilesetCache.Clear();
            _tileStates.Clear();
            _queuedGlbTileIds.Clear();
            _pendingTilesets.Clear();
            _pendingGlbTiles.Clear();
            _deferredGlbTiles.Clear();
            _outbound.Clear();

            _pendingTilesets.Enqueue(new PendingTileset(rootTileset, Matrix4x4d.Identity, string.Empty, 0, null, null), 0d);

            _logger.LogInformation(
                "Bootstrap discovery active (renderStartSpan={RenderStartSpan}m, range={Range}m, multiplier={Multiplier}).",
                _renderStartSpanM.ToString("F1", CultureInfo.InvariantCulture),
                options.RangeM.ToString("F1", CultureInfo.InvariantCulture),
                options.BootstrapRangeMultiplier.ToString("F2", CultureInfo.InvariantCulture));

            if (!options.DryRun)
            {
                _outbound.Enqueue(new UpdateLicenseCreditWorkItem("Google Maps"));
            }
        }

        public bool TryDequeueWorkItem(out SchedulerWorkItem? workItem)
        {
            EnsureInitialized();

            if (_stopped)
            {
                workItem = null;
                return false;
            }

            if (_outbound.Count == 0)
            {
                PlanUntilWorkAvailable();
            }

            if (_outbound.Count == 0)
            {
                workItem = null;
                return false;
            }

            workItem = _outbound.Dequeue();
            return true;
        }

        public void HandleResult(SchedulerWorkResult result)
        {
            EnsureInitialized();
            ArgumentNullException.ThrowIfNull(result);

            switch (result)
            {
                case ProcessNodeContentWorkResult processResult:
                    HandleProcessResult(processResult);
                    break;

                case RemoveParentTileSlotsWorkResult removeResult:
                    HandleRemoveResult(removeResult);
                    break;

                case UpdateLicenseCreditWorkResult updateResult:
                    HandleLicenseUpdateResult(updateResult);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported scheduler result type: {result.GetType().Name}");
            }
        }

        public RunSummary GetSummary()
        {
            EnsureInitialized();
            return new RunSummary(_candidateTiles, _processedTiles, _streamedMeshes, _failedTiles);
        }

        private void HandleProcessResult(ProcessNodeContentWorkResult result)
        {
            TileSelectionResult tile = result.Tile;
            string tileStateId = ResolveStableId(tile);
            TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, ResolveParentStableId(tile), tile.ContentKind);

            switch (result.Outcome)
            {
                case NestedTilesetContentOutcome nested:
                    _processedTiles++;
                    if (!_tilesetCache.ContainsKey(tile.ContentUri.AbsoluteUri))
                    {
                        _tilesetCache[tile.ContentUri.AbsoluteUri] = nested.Tileset;
                        _nestedTilesetFetches++;
                    }

                    _pendingTilesets.Enqueue(
                        new PendingTileset(
                            nested.Tileset,
                            tile.WorldTransform,
                            tile.TileId,
                            tile.Depth + 1,
                            tile.TileId,
                            ResolveStableId(tile)),
                        GetTraversalPriority(tile));
                    break;

                case StreamedRenderableContentOutcome streamed:
                    ApplyRenderableContentResult(tileStateId, tileState, streamed.StreamedMeshCount, streamed.SlotIds, streamed.AssetCopyright);
                    _streamedTileCount++;
                    _processedTiles++;
                    _logger.LogInformation(
                        "Processed tile {TileId} with {MeshCount} meshes (span={Span}m depth={Depth}).",
                        tile.TileId,
                        streamed.StreamedMeshCount,
                        tile.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a",
                        tile.Depth);
                    MarkTileCompleted(tileStateId);
                    break;

                case UnavailableContentOutcome unavailable:
                    _processedTiles++;
                    tileState.ChildrenDiscoveryDone = true;
                    MarkTileCompleted(tileStateId);
                    if (tile.ContentKind == TileContentKind.Json)
                    {
                        _logger.LogInformation("Skipped non-traversable JSON tile {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                    }
                    else
                    {
                        _logger.LogInformation("Skipped unavailable tile content {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                    }
                    break;

                case UnsupportedContentOutcome unsupported:
                    _processedTiles++;
                    tileState.ChildrenDiscoveryDone = true;
                    MarkTileCompleted(tileStateId);
                    _logger.LogInformation(
                        "Skipped unsupported tile content {TileId} ({Uri}){ReasonSuffix}",
                        tile.TileId,
                        tile.ContentUri,
                        string.IsNullOrWhiteSpace(unsupported.Reason) ? "." : $": {unsupported.Reason}");
                    break;

                case FailedContentOutcome failed:
                    tileState.ChildrenDiscoveryDone = true;
                    if (tile.ContentKind == TileContentKind.Glb)
                    {
                        ApplyRenderableContentResult(tileStateId, tileState, failed.StreamedMeshCount, failed.SlotIds ?? [], failed.AssetCopyright);
                    }

                    MarkTileCompleted(tileStateId);
                    _failedTiles++;
                    _logger.LogWarning(failed.Error, "Failed to process tile {TileId} from {Uri}", tile.TileId, tile.ContentUri);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported node content outcome type: {result.Outcome.GetType().Name}");
            }
        }

        private void ApplyRenderableContentResult(
            string tileStateId,
            TileLifecycle tileState,
            int streamedMeshCount,
            IReadOnlyList<string> slotIds,
            string? assetCopyright)
        {
            tileState.AttributionOwners = ParseAttributionOwners(
                string.IsNullOrWhiteSpace(assetCopyright)
                    ? []
                    : [assetCopyright]);
            RegisterAttributionOrder(tileState.AttributionOwners);

            foreach (string slotId in slotIds)
            {
                _ = tileState.SlotIds.Add(slotId);
            }

            if (slotIds.Count > 0)
            {
                MarkBranchVisible(tileStateId);
            }

            if (!Options.DryRun && slotIds.Count > 0 && TryActivateAttributions(tileState))
            {
                QueueLicenseCreditUpdate();
            }

            _streamedMeshes += streamedMeshCount;
        }

        private void HandleRemoveResult(RemoveParentTileSlotsWorkResult result)
        {
            if (!_tileStates.TryGetValue(result.StateId, out TileLifecycle? state))
            {
                return;
            }

            state.RemovalQueued = false;
            if (result.Succeeded)
            {
                state.Removed = true;
                if (TryDeactivateAttributions(state))
                {
                    QueueLicenseCreditUpdate();
                }

                return;
            }

            int failureCount = result.FailedSlotCount > 0 ? result.FailedSlotCount : 1;
            _failedTiles += failureCount;

            if (result.Error is not null)
            {
                _logger.LogWarning(result.Error, "Failed to remove parent tile slots for tile {TileId}.", result.TileId);
            }
            else
            {
                _logger.LogWarning("Failed to remove parent tile slots for tile {TileId}.", result.TileId);
            }
        }

        private void HandleLicenseUpdateResult(UpdateLicenseCreditWorkResult result)
        {
            if (result.Succeeded)
            {
                return;
            }

            _failedTiles++;
            if (result.Error is not null)
            {
                _logger.LogWarning(result.Error, "Failed to update session license credit.");
            }
            else
            {
                _logger.LogWarning("Failed to update session license credit.");
            }
        }

        private void PlanUntilWorkAvailable()
        {
            while (_outbound.Count == 0 && !_stopped)
            {
                QueueReadyDeferredFallbacks("loop-check");
                _logger.LogDebug(
                    "Bootstrap={Bootstrap} queues: streamable={Streamable}, deferred={Deferred}, pendingTilesets={PendingTilesets}.",
                    _bootstrapActive ? "active" : "inactive",
                    _pendingGlbTiles.Count,
                    _deferredGlbTiles.Count,
                    _pendingTilesets.Count);

                if (_streamedTileCount >= Options.MaxTiles)
                {
                    if (_pendingGlbTiles.Count > 0 || _pendingTilesets.Count > 0 || _deferredGlbTiles.Count > 0)
                    {
                        _logger.LogWarning(
                            "Stopped at tile limit ({MaxTiles}) with pending work (streamableGlb={PendingGlb}, deferredGlb={PendingDeferredGlb}, tilesets={PendingTilesets}). Increase tile-limit to reduce holes.",
                            Options.MaxTiles,
                            _pendingGlbTiles.Count,
                            _deferredGlbTiles.Count,
                            _pendingTilesets.Count);
                    }

                    _stopped = true;
                    break;
                }

                bool didWork = false;

                if (!_bootstrapActive && _pendingGlbTiles.Count > 0)
                {
                    didWork = true;
                    ScheduleNextGlbWork();
                    continue;
                }

                if (_pendingTilesets.Count > 0 && _nestedTilesetFetches < _maxNestedTilesetFetches)
                {
                    didWork = true;
                    ProcessTilesetSubtree(_pendingTilesets.Dequeue());
                    continue;
                }

                if (_pendingTilesets.Count > 0 &&
                    _nestedTilesetFetches >= _maxNestedTilesetFetches &&
                    _pendingGlbTiles.Count == 0 &&
                    _deferredGlbTiles.Count == 0)
                {
                    _logger.LogWarning(
                        "Stopped traversal because nested tileset fetch budget was reached ({MaxFetches}) and no streamable/deferred GLB tiles are queued.",
                        _maxNestedTilesetFetches);
                    _stopped = true;
                    break;
                }

                if (!didWork)
                {
                    _stopped = true;
                    break;
                }
            }
        }

        private void ProcessTilesetSubtree(PendingTileset tilesetWork)
        {
            if (tilesetWork.DepthOffset > Options.MaxDepth)
            {
                if (!string.IsNullOrWhiteSpace(tilesetWork.OwnerStableId))
                {
                    TileLifecycle ownerState = GetOrCreateTileState(tilesetWork.OwnerStableId, tilesetWork.OwnerTileId ?? "owner");
                    ownerState.SelfCompleted = true;
                    ownerState.ChildrenDiscoveryDone = true;
                    PropagateCompletion(ownerState.StateId);
                }

                return;
            }

            double effectiveDetailTargetM = _bootstrapActive
                ? SMath.Max(Options.DetailTargetM, _renderStartSpanM)
                : Options.DetailTargetM;

            IReadOnlyList<TileSelectionResult> selected = _selector.Select(
                tilesetWork.Tileset,
                Options.Reference,
                Range,
                Options.MaxDepth,
                effectiveDetailTargetM,
                maxTiles: 0,
                tilesetWork.ParentWorld,
                tilesetWork.IdPrefix,
                tilesetWork.DepthOffset,
                tilesetWork.OwnerTileId,
                tilesetWork.OwnerStableId);

            _logger.LogDebug(
                "Selected {Count} candidate tiles from subtree '{Prefix}' (detailTarget={DetailTarget}m, bootstrap={Bootstrap}).",
                selected.Count,
                tilesetWork.IdPrefix,
                effectiveDetailTargetM.ToString("F1", CultureInfo.InvariantCulture),
                _bootstrapActive ? "active" : "inactive");

            foreach (TileSelectionResult tile in selected)
            {
                RegisterTile(tile);
            }

            foreach (TileSelectionResult tile in selected
                         .OrderBy(static t => t.ContentKind == TileContentKind.Json ? 0 : 1)
                         .ThenBy(GetTraversalPriority))
            {
                string tileStateId = ResolveStableId(tile);
                TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, ResolveParentStableId(tile), tile.ContentKind);

                try
                {
                    switch (tile.ContentKind)
                    {
                        case TileContentKind.Json:
                            {
                                tileState.SelfCompleted = true;

                                if (tile.Depth >= Options.MaxDepth)
                                {
                                    _processedTiles++;
                                    tileState.ChildrenDiscoveryDone = true;
                                    PropagateCompletion(tileStateId);
                                    _logger.LogInformation("Skipped nested tileset at max depth for tile {TileId}.", tile.TileId);
                                    continue;
                                }

                                tileState.ChildrenDiscoveryDone = false;
                                if (_tilesetCache.TryGetValue(tile.ContentUri.AbsoluteUri, out Tileset? nestedTileset))
                                {
                                    _processedTiles++;
                                    var pendingCached = new PendingTileset(
                                        nestedTileset,
                                        tile.WorldTransform,
                                        tile.TileId,
                                        tile.Depth + 1,
                                        tile.TileId,
                                        ResolveStableId(tile));
                                    _pendingTilesets.Enqueue(pendingCached, GetTraversalPriority(tile));
                                }
                                else
                                {
                                    _outbound.Enqueue(new ProcessNodeContentWorkItem(tile));
                                }

                                continue;
                            }

                        case TileContentKind.Glb:
                            {
                                if (!_queuedGlbTileIds.Add(tileStateId))
                                {
                                    continue;
                                }

                                if (IsStreamableGlbCandidate(tile))
                                {
                                    tileState.DeferredSuppressed = false;
                                    tileState.FallbackQueued = false;
                                    _ = _deferredGlbTiles.Remove(tileStateId);
                                    _pendingGlbTiles.Enqueue(tile, GetTraversalPriority(tile));
                                    _logger.LogDebug(
                                        "Queued streamable GLB tile {TileId} (depth={Depth}, span={Span}m, hasChildren={HasChildren}).",
                                        tile.TileId,
                                        tile.Depth,
                                        tile.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a",
                                        tile.HasChildren);
                                    TryDeactivateBootstrap("streamable-glb-discovered", tile);
                                }
                                else
                                {
                                    tileState.DeferredSuppressed = true;
                                    _deferredGlbTiles[tileStateId] = tile;
                                    _logger.LogDebug(
                                        "Deferred coarse GLB tile {TileId} (depth={Depth}, span={Span}m, threshold={Threshold}m).",
                                        tile.TileId,
                                        tile.Depth,
                                        tile.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a",
                                        _renderStartSpanM.ToString("F1", CultureInfo.InvariantCulture));
                                }

                                continue;
                            }

                        case TileContentKind.Other:
                            break;

                        default:
                            {
                                _processedTiles++;
                                tileState.ChildrenDiscoveryDone = true;
                                MarkTileCompleted(tileStateId);
                                _logger.LogInformation("Skipped unsupported tile content {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                                continue;
                            }
                    }
                }
                catch (Exception ex)
                {
                    _failedTiles++;
                    tileState.ChildrenDiscoveryDone = true;
                    MarkTileCompleted(tileStateId);
                    _logger.LogWarning(ex, "Failed while discovering tile {TileId} from {Uri}", tile.TileId, tile.ContentUri);
                }
            }

            QueueReadyDeferredFallbacks("subtree-processed");

            if (!string.IsNullOrWhiteSpace(tilesetWork.OwnerStableId))
            {
                TileLifecycle owner = GetOrCreateTileState(tilesetWork.OwnerStableId, tilesetWork.OwnerTileId ?? "owner");
                owner.SelfCompleted = true;
                owner.ChildrenDiscoveryDone = true;
                PropagateCompletion(owner.StateId);
                QueueReadyDeferredFallbacks("owner-completed");
            }
        }

        private void ScheduleNextGlbWork()
        {
            if (_pendingGlbTiles.Count == 0)
            {
                return;
            }

            TileSelectionResult tile = _pendingGlbTiles.Dequeue();
            _candidateTiles++;
            _outbound.Enqueue(new ProcessNodeContentWorkItem(tile));
        }

        private double GetTraversalPriority(TileSelectionResult tile)
        {
            double span = tile.HorizontalSpanM ?? 1_000_000_000d;
            int depth = tile.Depth;
            double leafBias = tile.HasChildren ? 0d : -0.1d;
            return (depth * 1_000_000_000_000d) - span + leafBias;
        }

        private bool IsStreamableGlbCandidate(TileSelectionResult tile)
        {
            return tile.HorizontalSpanM is null ||
                tile.HorizontalSpanM.Value <= _renderStartSpanM ||
                !tile.HasChildren;
        }

        private void TryDeactivateBootstrap(string reason, TileSelectionResult? triggerTile = null)
        {
            if (!_bootstrapActive || _pendingGlbTiles.Count == 0)
            {
                return;
            }

            _bootstrapActive = false;
            _logger.LogInformation(
                "Bootstrap discovery complete: reason={Reason}, streamable={Streamable}, deferred={Deferred}, pendingTilesets={PendingTilesets}, triggerTile={TileId}, triggerDepth={Depth}, triggerSpan={Span}m.",
                reason,
                _pendingGlbTiles.Count,
                _deferredGlbTiles.Count,
                _pendingTilesets.Count,
                triggerTile?.TileId ?? "n/a",
                triggerTile?.Depth.ToString(CultureInfo.InvariantCulture) ?? "n/a",
                triggerTile?.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a");
        }

        private static string? NormalizeAttributionOwner(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static IReadOnlyList<string> ParseAttributionOwners(IEnumerable<string> values)
        {
            var owners = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string[] segments = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string segment in segments)
                {
                    string? normalized = NormalizeAttributionOwner(segment);
                    if (normalized is null || !seen.Add(normalized))
                    {
                        continue;
                    }

                    owners.Add(normalized);
                }
            }

            return owners;
        }

        private void RegisterAttributionOrder(IEnumerable<string> rawValues)
        {
            foreach (string normalized in ParseAttributionOwners(rawValues))
            {
                if (_knownAttributions.Add(normalized))
                {
                    _attributionOrder.Add(normalized);
                }
            }
        }

        private bool TryActivateAttributions(TileLifecycle state)
        {
            if (state.AttributionsApplied)
            {
                return false;
            }

            bool changed = false;
            foreach (string normalized in state.AttributionOwners)
            {
                _activeAttributionCounts[normalized] = _activeAttributionCounts.TryGetValue(normalized, out int count) ? count + 1 : 1;
                changed = true;
            }

            state.AttributionsApplied = true;
            return changed;
        }

        private bool TryDeactivateAttributions(TileLifecycle state)
        {
            if (!state.AttributionsApplied)
            {
                return false;
            }

            bool changed = false;
            foreach (string normalized in state.AttributionOwners)
            {
                if (!_activeAttributionCounts.TryGetValue(normalized, out int count))
                {
                    continue;
                }

                if (count <= 1)
                {
                    _ = _activeAttributionCounts.Remove(normalized);
                }
                else
                {
                    _activeAttributionCounts[normalized] = count - 1;
                }

                changed = true;
            }

            state.AttributionsApplied = false;
            return changed;
        }

        private void QueueLicenseCreditUpdate()
        {
            if (Options.DryRun)
            {
                return;
            }

            _outbound.Enqueue(new UpdateLicenseCreditWorkItem(
                BuildLicenseCreditString(_attributionOrder, _activeAttributionCounts)));
        }

        private TileLifecycle GetOrCreateTileState(
            string stateId,
            string tileId,
            string? parentStateId = null,
            TileContentKind? contentKind = null)
        {
            if (!_tileStates.TryGetValue(stateId, out TileLifecycle? state))
            {
                state = new TileLifecycle(stateId, tileId)
                {
                    ParentStateId = parentStateId
                };

                if (contentKind.HasValue)
                {
                    state.ContentKind = contentKind.Value;
                }

                _tileStates[stateId] = state;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(parentStateId) && string.IsNullOrWhiteSpace(state.ParentStateId))
                {
                    state.ParentStateId = parentStateId;
                }

                if (contentKind.HasValue)
                {
                    state.ContentKind = contentKind.Value;
                }
            }

            return state;
        }

        private void RegisterTile(TileSelectionResult tile)
        {
            string tileStateId = ResolveStableId(tile);
            string? parentStateId = ResolveParentStableId(tile);
            TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, parentStateId, tile.ContentKind);
            if (string.IsNullOrWhiteSpace(parentStateId))
            {
                return;
            }

            TileLifecycle parentState = GetOrCreateTileState(parentStateId, tile.ParentTileId ?? "unknown");
            _ = parentState.DirectChildren.Add(tileStateId);
            _ = tileState.BranchCompleted
                ? parentState.PendingChildBranches.Remove(tileStateId)
                : parentState.PendingChildBranches.Add(tileStateId);
        }

        private static bool IsBranchCompleteForParent(TileLifecycle state)
        {
            return state.ContentKind switch
            {
                TileContentKind.Glb => state.SelfCompleted || (state.DeferredSuppressed && state.ChildrenDiscoveryDone && state.PendingChildBranches.Count == 0),
                TileContentKind.Json => state.SelfCompleted && state.ChildrenDiscoveryDone && state.PendingChildBranches.Count == 0,
                _ => state.SelfCompleted
            };
        }

        private void TryQueueParentRemovalIfReady(TileLifecycle state)
        {
            if (state.Removed ||
                state.RemovalQueued ||
                state.ContentKind != TileContentKind.Glb ||
                !state.SelfCompleted ||
                !state.ChildrenDiscoveryDone ||
                state.DirectChildren.Count == 0 ||
                state.PendingChildBranches.Count > 0)
            {
                return;
            }

            if (Options.DryRun)
            {
                state.Removed = true;
                return;
            }

            if (state.SlotIds.Count == 0)
            {
                state.Removed = true;
                if (TryDeactivateAttributions(state))
                {
                    QueueLicenseCreditUpdate();
                }

                return;
            }

            state.RemovalQueued = true;
            _outbound.Enqueue(new RemoveParentTileSlotsWorkItem(state.StateId, state.TileId, state.SlotIds.ToList()));
        }

        private void PropagateCompletion(string stateId)
        {
            var queue = new Queue<string>();
            queue.Enqueue(stateId);

            while (queue.Count > 0)
            {
                string currentId = queue.Dequeue();
                if (!_tileStates.TryGetValue(currentId, out TileLifecycle? state))
                {
                    continue;
                }

                if (TryQueueDeferredFallback(state, "branch-complete"))
                {
                    continue;
                }

                TryQueueParentRemovalIfReady(state);

                if (state.BranchCompleted || !IsBranchCompleteForParent(state))
                {
                    continue;
                }

                state.BranchCompleted = true;
                if (state.DeferredSuppressed && !state.SelfCompleted)
                {
                    _ = _deferredGlbTiles.Remove(state.StateId);
                }

                if (string.IsNullOrWhiteSpace(state.ParentStateId))
                {
                    continue;
                }

                if (!_tileStates.TryGetValue(state.ParentStateId, out TileLifecycle? parentState))
                {
                    continue;
                }

                _ = parentState.PendingChildBranches.Remove(state.StateId);
                queue.Enqueue(parentState.StateId);
            }
        }

        private void MarkTileCompleted(string stateId)
        {
            if (!_tileStates.TryGetValue(stateId, out TileLifecycle? state))
            {
                return;
            }

            state.SelfCompleted = true;
            PropagateCompletion(stateId);
        }

        private void MarkBranchVisible(string stateId)
        {
            string? currentId = stateId;
            while (!string.IsNullOrWhiteSpace(currentId) &&
                   _tileStates.TryGetValue(currentId, out TileLifecycle? current))
            {
                if (current.BranchHasVisibleContent)
                {
                    break;
                }

                current.BranchHasVisibleContent = true;
                currentId = current.ParentStateId;
            }
        }

        private bool TryQueueDeferredFallback(TileLifecycle state, string reason)
        {
            if (!state.DeferredSuppressed ||
                state.SelfCompleted ||
                state.FallbackQueued ||
                !state.ChildrenDiscoveryDone ||
                state.PendingChildBranches.Count > 0 ||
                state.BranchHasVisibleContent ||
                !_deferredGlbTiles.TryGetValue(state.StateId, out TileSelectionResult? deferredTile))
            {
                return false;
            }

            if (_pendingTilesets.Count > 0)
            {
                return false;
            }

            _ = _deferredGlbTiles.Remove(state.StateId);
            _pendingGlbTiles.Enqueue(deferredTile, GetTraversalPriority(deferredTile));
            state.FallbackQueued = true;

            _logger.LogDebug(
                "Queued deferred fallback tile {TileId} (reason={Reason}, depth={Depth}, span={Span}m).",
                deferredTile.TileId,
                reason,
                deferredTile.Depth,
                deferredTile.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a");

            TryDeactivateBootstrap("deferred-fallback", deferredTile);
            return true;
        }

        private void QueueReadyDeferredFallbacks(string reason)
        {
            if (_deferredGlbTiles.Count == 0)
            {
                return;
            }

            foreach (string tileId in _deferredGlbTiles.Keys.ToList())
            {
                if (!_tileStates.TryGetValue(tileId, out TileLifecycle? state))
                {
                    continue;
                }

                _ = TryQueueDeferredFallback(state, reason);
            }
        }

        private static string BuildLicenseCreditString(
            IReadOnlyList<string> attributionOrder,
            IReadOnlyDictionary<string, int> activeAttributionCounts)
        {
            var active = new List<(string Owner, int Count, int Order)>(attributionOrder.Count);
            var orderIndex = new Dictionary<string, int>(attributionOrder.Count, StringComparer.Ordinal);
            for (int i = 0; i < attributionOrder.Count; i++)
            {
                orderIndex[attributionOrder[i]] = i;
            }

            foreach (string value in attributionOrder)
            {
                if (activeAttributionCounts.TryGetValue(value, out int count) && count > 0)
                {
                    active.Add((value, count, orderIndex[value]));
                }
            }

            if (active.Count == 0)
            {
                return "Google Maps";
            }

            IEnumerable<string> ordered = active
                .OrderByDescending(static x => x.Count)
                .ThenBy(static x => x.Order)
                .Select(static x => x.Owner);
            return string.Join("; ", ordered);
        }

        private static string ResolveStableId(TileSelectionResult tile)
        {
            return string.IsNullOrWhiteSpace(tile.StableId)
                ? tile.TileId
                : tile.StableId!;
        }

        private static string? ResolveParentStableId(TileSelectionResult tile)
        {
            return string.IsNullOrWhiteSpace(tile.ParentStableId)
                ? tile.ParentTileId
                : tile.ParentStableId;
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Scheduler is not initialized.");
            }
        }

        private sealed record PendingTileset(
            Tileset Tileset,
            Matrix4x4d ParentWorld,
            string IdPrefix,
            int DepthOffset,
            string? OwnerTileId,
            string? OwnerStableId);

        private sealed class TileLifecycle(string stateId, string tileId)
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
