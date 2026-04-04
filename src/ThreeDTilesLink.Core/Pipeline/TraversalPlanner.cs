using System.Globalization;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class TraversalPlanner(
        ITileSelector selector,
        ILogger<TraversalPlanner> logger)
    {
        private readonly ITileSelector _selector = selector;
        private readonly ILogger<TraversalPlanner> _logger = logger;
        private readonly PlannerState _state = new();
        private readonly LicenseCreditAggregator _licenseCredits = new();

        private TileRunRequest Request => _state.Request ?? throw new InvalidOperationException("Planner is not initialized.");
        private TraversalOptions Traversal => Request.Traversal;
        private ResoniteOutputOptions Output => Request.Output;
        private QueryRange Range => _state.Range ?? throw new InvalidOperationException("Planner is not initialized.");

        public void Initialize(Tileset rootTileset, TileRunRequest request)
        {
            ArgumentNullException.ThrowIfNull(rootTileset);
            ArgumentNullException.ThrowIfNull(request);

            _state.Request = request;
            _state.Range = new QueryRange(request.Traversal.RangeM);
            _state.Initialized = true;
            _state.Stopped = false;
            _state.BootstrapActive = true;
            _state.RenderStartSpanM = request.Traversal.RangeM * (request.Traversal.BootstrapRangeMultiplier > 0d ? request.Traversal.BootstrapRangeMultiplier : 4d);
            _state.MaxNestedTilesetFetches = SMath.Max(request.Traversal.MaxTiles * 64, 512);
            _state.NestedTilesetFetches = 0;
            _state.StreamedMeshes = 0;
            _state.FailedTiles = 0;
            _state.ProcessedTiles = 0;
            _state.CandidateTiles = 0;
            _state.StreamedTileCount = 0;
            _state.SelectedTileStateIds.Clear();
            _state.TilesetCache.Clear();
            _state.TileStates.Clear();
            _state.QueuedGlbTileIds.Clear();
            _state.PendingTilesets.Clear();
            _state.PendingGlbTiles.Clear();
            _state.DeferredGlbTiles.Clear();
            _state.Outbound.Clear();
            _licenseCredits.Reset();

            _state.PendingTilesets.Enqueue(new PlannerState.PendingTileset(rootTileset, Matrix4x4d.Identity, string.Empty, 0, null, null), 0d);

            _logger.LogInformation(
                "Bootstrap discovery active (renderStartSpan={RenderStartSpan}m, range={Range}m, multiplier={Multiplier}).",
                _state.RenderStartSpanM.ToString("F1", CultureInfo.InvariantCulture),
                request.Traversal.RangeM.ToString("F1", CultureInfo.InvariantCulture),
                request.Traversal.BootstrapRangeMultiplier.ToString("F2", CultureInfo.InvariantCulture));

            if (!request.Output.DryRun)
            {
                _state.Outbound.Enqueue(new UpdateLicenseCreditCommand("Google Maps"));
            }
        }

        public bool TryPlanNext(out PlannerCommand? command)
        {
            if (!TryPlanNextBatch(1, out IReadOnlyList<PlannerCommand> commands))
            {
                command = null;
                return false;
            }

            command = commands[0];
            return true;
        }

        public bool TryPlanNextBatch(int maxCount, out IReadOnlyList<PlannerCommand> commands)
        {
            EnsureInitialized();
            if (maxCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCount), "Batch size must be positive.");
            }

            if (_state.Outbound.Count > 0)
            {
                commands = DequeueNextBatch(maxCount);
                return commands.Count > 0;
            }

            if (_state.Stopped)
            {
                commands = [];
                return false;
            }

            bool plannedFromEmptyQueue = false;
            plannedFromEmptyQueue = true;
            PlanUntilWorkAvailable();

            if (_state.Outbound.Count == 0)
            {
                commands = [];
                return false;
            }

            if (plannedFromEmptyQueue && maxCount > 1)
            {
                QueueAdditionalReadyProcessCommands(maxCount);
            }

            commands = DequeueNextBatch(maxCount);
            return commands.Count > 0;
        }

        public void ApplyResult(PlannerResult result)
        {
            EnsureInitialized();
            ArgumentNullException.ThrowIfNull(result);

            switch (result)
            {
                case NestedTilesetLoadedResult nested:
                    HandleNestedTilesetLoaded(nested);
                    break;
                case RenderableContentReadyResult renderable:
                    HandleRenderableContentReady(renderable);
                    break;
                case ContentSkippedResult skipped:
                    HandleContentSkipped(skipped);
                    break;
                case ContentFailedResult failed:
                    HandleContentFailed(failed);
                    break;
                case SlotsRemovedResult removed:
                    HandleSlotsRemoved(removed);
                    break;
                case LicenseUpdatedResult updated:
                    HandleLicenseUpdated(updated);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported planner result type: {result.GetType().Name}");
            }
        }

        public RunSummary GetSummary()
        {
            EnsureInitialized();
            return new RunSummary(_state.CandidateTiles, _state.ProcessedTiles, _state.StreamedMeshes, _state.FailedTiles);
        }

        public IReadOnlySet<string> GetSelectedTileStableIds()
        {
            EnsureInitialized();
            return new HashSet<string>(_state.SelectedTileStateIds, StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, RetainedTileState> GetVisibleTiles()
        {
            EnsureInitialized();

            return _state.TileStates
                .Values
                .Where(static state => !state.Removed && state.SlotIds.Count > 0)
                .ToDictionary(
                    static state => state.StateId,
                    state => new RetainedTileState(
                        state.StateId,
                        state.TileId,
                        state.ParentStateId,
                        GetAncestorStableIds(state),
                        state.SlotIds.ToArray(),
                        state.AssetCopyright),
                    StringComparer.Ordinal);
        }

        private IReadOnlyList<string> GetAncestorStableIds(PlannerState.TileLifecycle state)
        {
            var ancestors = new List<string>();
            string? currentId = state.ParentStateId;
            while (!string.IsNullOrWhiteSpace(currentId))
            {
                ancestors.Add(currentId);
                currentId = _state.TileStates.TryGetValue(currentId, out PlannerState.TileLifecycle? parentState)
                    ? parentState.ParentStateId
                    : null;
            }

            return ancestors;
        }

        public PlannerProgress GetProgress()
        {
            EnsureInitialized();
            return new PlannerProgress(
                _state.CandidateTiles,
                _state.ProcessedTiles,
                _state.StreamedMeshes,
                _state.FailedTiles,
                _state.PendingTilesets.Count,
                _state.PendingGlbTiles.Count,
                _state.DeferredGlbTiles.Count,
                _state.Outbound.Count(static command => command.Kind == PlannerCommandKind.ProcessTileContent));
        }

        private void HandleNestedTilesetLoaded(NestedTilesetLoadedResult result)
        {
            TileSelectionResult tile = result.Tile;
            string tileStateId = ResolveStableId(tile);
            PlannerState.TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, ResolveParentStableId(tile), tile.ContentKind);

            _state.ProcessedTiles++;
            if (!_state.TilesetCache.ContainsKey(tile.ContentUri.AbsoluteUri))
            {
                _state.TilesetCache[tile.ContentUri.AbsoluteUri] = result.Tileset;
                _state.NestedTilesetFetches++;
            }

            _state.PendingTilesets.Enqueue(
                new PlannerState.PendingTileset(
                    result.Tileset,
                    tile.WorldTransform,
                    tile.TileId,
                    tile.Depth + 1,
                    tile.TileId,
                    ResolveStableId(tile)),
                GetTraversalPriority(tile));

            tileState.ChildrenDiscoveryDone = true;
        }

        private void HandleRenderableContentReady(RenderableContentReadyResult result)
        {
            TileSelectionResult tile = result.Tile;
            string tileStateId = ResolveStableId(tile);
            PlannerState.TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, ResolveParentStableId(tile), tile.ContentKind);

            ApplyRenderableContentResult(tileStateId, tileState, result.StreamedMeshCount, result.SlotIds, result.AssetCopyright);
            _state.StreamedTileCount++;
            _state.ProcessedTiles++;
            _logger.LogInformation(
                "Processed tile {TileId} with {MeshCount} meshes (span={Span}m depth={Depth}).",
                tile.TileId,
                result.StreamedMeshCount,
                tile.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a",
                tile.Depth);
            MarkTileCompleted(tileStateId);
        }

        private void HandleContentSkipped(ContentSkippedResult result)
        {
            TileSelectionResult tile = result.Tile;
            string tileStateId = ResolveStableId(tile);
            PlannerState.TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, ResolveParentStableId(tile), tile.ContentKind);

            _state.ProcessedTiles++;
            tileState.ChildrenDiscoveryDone = true;
            MarkTileCompleted(tileStateId);

            if (result.Error is not null)
            {
                _logger.LogInformation(result.Error, "Skipped unavailable tile content {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                return;
            }

            if (tile.ContentKind == TileContentKind.Json)
            {
                _logger.LogInformation("Skipped non-traversable JSON tile {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
            }
            else
            {
                _logger.LogInformation(
                    "Skipped unsupported tile content {TileId} ({Uri}){ReasonSuffix}",
                    tile.TileId,
                    tile.ContentUri,
                    string.IsNullOrWhiteSpace(result.Reason) ? "." : $": {result.Reason}");
            }
        }

        private void HandleContentFailed(ContentFailedResult result)
        {
            TileSelectionResult tile = result.Tile;
            string tileStateId = ResolveStableId(tile);
            PlannerState.TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, ResolveParentStableId(tile), tile.ContentKind);

            tileState.ChildrenDiscoveryDone = true;
            if (tile.ContentKind == TileContentKind.Glb)
            {
                ApplyRenderableContentResult(tileStateId, tileState, result.StreamedMeshCount, result.SlotIds ?? [], result.AssetCopyright);
            }

            MarkTileCompleted(tileStateId);
            _state.FailedTiles++;
            _logger.LogWarning(result.Error, "Failed to process tile {TileId} from {Uri}", tile.TileId, tile.ContentUri);
        }

        private void ApplyRenderableContentResult(
            string tileStateId,
            PlannerState.TileLifecycle tileState,
            int streamedMeshCount,
            IReadOnlyList<string> slotIds,
            string? assetCopyright)
        {
            tileState.AttributionOwners = LicenseCreditAggregator.ParseOwners(
                string.IsNullOrWhiteSpace(assetCopyright)
                    ? []
                    : [assetCopyright]);
            tileState.AssetCopyright = assetCopyright;
            _licenseCredits.RegisterOrder(tileState.AttributionOwners);

            foreach (string slotId in slotIds)
            {
                _ = tileState.SlotIds.Add(slotId);
            }

            if (slotIds.Count > 0)
            {
                MarkBranchVisible(tileStateId);
            }

            if (!Output.DryRun && slotIds.Count > 0 && TryActivateAttributions(tileState))
            {
                QueueLicenseCreditUpdate();
            }

            _state.StreamedMeshes += streamedMeshCount;
        }

        private void HandleSlotsRemoved(SlotsRemovedResult result)
        {
            if (!_state.TileStates.TryGetValue(result.StateId, out PlannerState.TileLifecycle? state))
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
            _state.FailedTiles += failureCount;

            if (result.Error is not null)
            {
                _logger.LogWarning(result.Error, "Failed to remove parent tile slots for tile {TileId}.", result.TileId);
            }
            else
            {
                _logger.LogWarning("Failed to remove parent tile slots for tile {TileId}.", result.TileId);
            }
        }

        private void HandleLicenseUpdated(LicenseUpdatedResult result)
        {
            if (result.Succeeded)
            {
                return;
            }

            _state.FailedTiles++;
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
            while (_state.Outbound.Count == 0 && !_state.Stopped)
            {
                QueueReadyDeferredFallbacks("loop-check");
                _logger.LogDebug(
                    "Bootstrap={Bootstrap} queues: streamable={Streamable}, deferred={Deferred}, pendingTilesets={PendingTilesets}.",
                    _state.BootstrapActive ? "active" : "inactive",
                    _state.PendingGlbTiles.Count,
                    _state.DeferredGlbTiles.Count,
                    _state.PendingTilesets.Count);

                if (_state.StreamedTileCount >= Traversal.MaxTiles)
                {
                    if (_state.PendingGlbTiles.Count > 0 || _state.PendingTilesets.Count > 0 || _state.DeferredGlbTiles.Count > 0)
                    {
                        _logger.LogWarning(
                            "Stopped at tile limit ({MaxTiles}) with pending work (streamableGlb={PendingGlb}, deferredGlb={PendingDeferredGlb}, tilesets={PendingTilesets}). Increase tile-limit to reduce holes.",
                            Traversal.MaxTiles,
                            _state.PendingGlbTiles.Count,
                            _state.DeferredGlbTiles.Count,
                            _state.PendingTilesets.Count);
                    }

                    _state.Stopped = true;
                    break;
                }

                bool didWork = false;

                if (!_state.BootstrapActive && _state.PendingGlbTiles.Count > 0)
                {
                    didWork = true;
                    ScheduleNextGlbWork();
                    continue;
                }

                if (_state.PendingTilesets.Count > 0 && _state.NestedTilesetFetches < _state.MaxNestedTilesetFetches)
                {
                    didWork = true;
                    ProcessTilesetSubtree(_state.PendingTilesets.Dequeue());
                    continue;
                }

                if (_state.PendingTilesets.Count > 0 &&
                    _state.NestedTilesetFetches >= _state.MaxNestedTilesetFetches &&
                    _state.PendingGlbTiles.Count == 0 &&
                    _state.DeferredGlbTiles.Count == 0)
                {
                    _logger.LogWarning(
                        "Stopped traversal because nested tileset fetch budget was reached ({MaxFetches}) and no streamable/deferred GLB tiles are queued.",
                        _state.MaxNestedTilesetFetches);
                    _state.Stopped = true;
                    break;
                }

                if (!didWork)
                {
                    _state.Stopped = true;
                    break;
                }
            }
        }

        private void ProcessTilesetSubtree(PlannerState.PendingTileset tilesetWork)
        {
            if (tilesetWork.DepthOffset > Traversal.MaxDepth)
            {
                if (!string.IsNullOrWhiteSpace(tilesetWork.OwnerStableId))
                {
                    PlannerState.TileLifecycle ownerState = GetOrCreateTileState(tilesetWork.OwnerStableId, tilesetWork.OwnerTileId ?? "owner");
                    ownerState.SelfCompleted = true;
                    ownerState.ChildrenDiscoveryDone = true;
                    PropagateCompletion(ownerState.StateId);
                }

                return;
            }

            double effectiveDetailTargetM = _state.BootstrapActive
                ? SMath.Max(Traversal.DetailTargetM, _state.RenderStartSpanM)
                : Traversal.DetailTargetM;

            IReadOnlyList<TileSelectionResult> selected = _selector.Select(
                tilesetWork.Tileset,
                Request.SelectionReference,
                Range,
                Traversal.MaxDepth,
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
                _state.BootstrapActive ? "active" : "inactive");

            foreach (TileSelectionResult tile in selected)
            {
                RegisterTile(tile);
            }

            foreach (TileSelectionResult tile in selected
                         .OrderBy(static t => t.ContentKind == TileContentKind.Json ? 0 : 1)
                         .ThenBy(GetTraversalPriority))
            {
                string tileStateId = ResolveStableId(tile);
                PlannerState.TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, ResolveParentStableId(tile), tile.ContentKind);

                try
                {
                    switch (tile.ContentKind)
                    {
                        case TileContentKind.Json:
                            tileState.SelfCompleted = true;

                            if (tile.Depth >= Traversal.MaxDepth)
                            {
                                _state.ProcessedTiles++;
                                tileState.ChildrenDiscoveryDone = true;
                                PropagateCompletion(tileStateId);
                                _logger.LogInformation("Skipped nested tileset at max depth for tile {TileId}.", tile.TileId);
                                continue;
                            }

                            tileState.ChildrenDiscoveryDone = false;
                            if (_state.TilesetCache.TryGetValue(tile.ContentUri.AbsoluteUri, out Tileset? nestedTileset))
                            {
                                _state.ProcessedTiles++;
                                _state.PendingTilesets.Enqueue(
                                    new PlannerState.PendingTileset(
                                        nestedTileset,
                                        tile.WorldTransform,
                                        tile.TileId,
                                        tile.Depth + 1,
                                        tile.TileId,
                                        ResolveStableId(tile)),
                                    GetTraversalPriority(tile));
                            }
                            else
                            {
                                _state.Outbound.Enqueue(new ProcessTileContentCommand(tile));
                            }

                            continue;

                        case TileContentKind.Glb:
                            if (!_state.QueuedGlbTileIds.Add(tileStateId))
                            {
                                continue;
                            }

                            if (IsStreamableGlbCandidate(tile))
                            {
                                tileState.DeferredSuppressed = false;
                                tileState.FallbackQueued = false;
                                _ = _state.DeferredGlbTiles.Remove(tileStateId);
                                _state.PendingGlbTiles.Enqueue(tile, GetTraversalPriority(tile));
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
                                _state.DeferredGlbTiles[tileStateId] = tile;
                                _logger.LogDebug(
                                    "Deferred coarse GLB tile {TileId} (depth={Depth}, span={Span}m, threshold={Threshold}m).",
                                    tile.TileId,
                                    tile.Depth,
                                    tile.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a",
                                    _state.RenderStartSpanM.ToString("F1", CultureInfo.InvariantCulture));
                            }

                            continue;

                        case TileContentKind.Other:
                            _state.ProcessedTiles++;
                            tileState.ChildrenDiscoveryDone = true;
                            MarkTileCompleted(tileStateId);
                            _logger.LogInformation("Skipped unsupported tile content {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                            continue;
                    }
                }
                catch (Exception ex)
                {
                    _state.FailedTiles++;
                    tileState.ChildrenDiscoveryDone = true;
                    MarkTileCompleted(tileStateId);
                    _logger.LogWarning(ex, "Failed while discovering tile {TileId} from {Uri}", tile.TileId, tile.ContentUri);
                }
            }

            QueueReadyDeferredFallbacks("subtree-processed");

            if (!string.IsNullOrWhiteSpace(tilesetWork.OwnerStableId))
            {
                PlannerState.TileLifecycle owner = GetOrCreateTileState(tilesetWork.OwnerStableId, tilesetWork.OwnerTileId ?? "owner");
                owner.SelfCompleted = true;
                owner.ChildrenDiscoveryDone = true;
                PropagateCompletion(owner.StateId);
                QueueReadyDeferredFallbacks("owner-completed");
            }
        }

        private void ScheduleNextGlbWork()
        {
            if (_state.PendingGlbTiles.Count == 0)
            {
                return;
            }

            TileSelectionResult tile = _state.PendingGlbTiles.Dequeue();
            _state.CandidateTiles++;
            _state.Outbound.Enqueue(new ProcessTileContentCommand(tile));
        }

        private void QueueAdditionalReadyProcessCommands(int maxCount)
        {
            if (_state.BootstrapActive)
            {
                return;
            }

            while (_state.Outbound.Count < maxCount &&
                   _state.PendingGlbTiles.Count > 0 &&
                   _state.Outbound.All(static command => command.Kind == PlannerCommandKind.ProcessTileContent))
            {
                ScheduleNextGlbWork();
            }
        }

        private IReadOnlyList<PlannerCommand> DequeueNextBatch(int maxCount)
        {
            if (_state.Outbound.Count == 0)
            {
                return [];
            }

            PlannerCommand first = _state.Outbound.Dequeue();
            if (maxCount == 1 || first.Kind != PlannerCommandKind.ProcessTileContent)
            {
                return [first];
            }

            var batch = new List<PlannerCommand>(maxCount)
            {
                first
            };

            while (batch.Count < maxCount &&
                   _state.Outbound.Count > 0 &&
                   _state.Outbound.Peek().Kind == PlannerCommandKind.ProcessTileContent)
            {
                batch.Add(_state.Outbound.Dequeue());
            }

            return batch;
        }

        private double GetTraversalPriority(TileSelectionResult tile)
        {
            double span = tile.HorizontalSpanM ?? 1_000_000_000d;
            double leafBias = tile.HasChildren ? 0d : -0.1d;
            return (tile.Depth * 1_000_000_000_000d) - span + leafBias;
        }

        private bool IsStreamableGlbCandidate(TileSelectionResult tile)
        {
            return tile.HorizontalSpanM is null ||
                tile.HorizontalSpanM.Value <= _state.RenderStartSpanM ||
                !tile.HasChildren;
        }

        private void TryDeactivateBootstrap(string reason, TileSelectionResult? triggerTile = null)
        {
            if (!_state.BootstrapActive || _state.PendingGlbTiles.Count == 0)
            {
                return;
            }

            _state.BootstrapActive = false;
            _logger.LogInformation(
                "Bootstrap discovery complete: reason={Reason}, streamable={Streamable}, deferred={Deferred}, pendingTilesets={PendingTilesets}, triggerTile={TileId}, triggerDepth={Depth}, triggerSpan={Span}m.",
                reason,
                _state.PendingGlbTiles.Count,
                _state.DeferredGlbTiles.Count,
                _state.PendingTilesets.Count,
                triggerTile?.TileId ?? "n/a",
                triggerTile?.Depth.ToString(CultureInfo.InvariantCulture) ?? "n/a",
                triggerTile?.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a");
        }

        private bool TryActivateAttributions(PlannerState.TileLifecycle state)
        {
            if (state.AttributionsApplied)
            {
                return false;
            }

            bool changed = _licenseCredits.Activate(state.AttributionOwners);
            state.AttributionsApplied = changed || state.AttributionOwners.Count > 0;
            return changed;
        }

        private bool TryDeactivateAttributions(PlannerState.TileLifecycle state)
        {
            if (!state.AttributionsApplied)
            {
                return false;
            }

            bool changed = _licenseCredits.Deactivate(state.AttributionOwners);
            state.AttributionsApplied = false;
            return changed;
        }

        private void QueueLicenseCreditUpdate()
        {
            if (Output.DryRun)
            {
                return;
            }

            _state.Outbound.Enqueue(new UpdateLicenseCreditCommand(_licenseCredits.BuildCreditString()));
        }

        private PlannerState.TileLifecycle GetOrCreateTileState(
            string stateId,
            string tileId,
            string? parentStateId = null,
            TileContentKind? contentKind = null)
        {
            if (!_state.TileStates.TryGetValue(stateId, out PlannerState.TileLifecycle? state))
            {
                state = new PlannerState.TileLifecycle(stateId, tileId)
                {
                    ParentStateId = parentStateId
                };

                if (contentKind.HasValue)
                {
                    state.ContentKind = contentKind.Value;
                }

                _state.TileStates[stateId] = state;
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
            _ = _state.SelectedTileStateIds.Add(tileStateId);
            string? parentStateId = ResolveParentStableId(tile);
            PlannerState.TileLifecycle tileState = GetOrCreateTileState(tileStateId, tile.TileId, parentStateId, tile.ContentKind);
            if (string.IsNullOrWhiteSpace(parentStateId))
            {
                return;
            }

            PlannerState.TileLifecycle parentState = GetOrCreateTileState(parentStateId, tile.ParentTileId ?? "unknown");
            _ = parentState.DirectChildren.Add(tileStateId);
            _ = tileState.BranchCompleted
                ? parentState.PendingChildBranches.Remove(tileStateId)
                : parentState.PendingChildBranches.Add(tileStateId);
        }

        private static bool IsBranchCompleteForParent(PlannerState.TileLifecycle state)
        {
            return state.ContentKind switch
            {
                TileContentKind.Glb => state.SelfCompleted || (state.DeferredSuppressed && state.ChildrenDiscoveryDone && state.PendingChildBranches.Count == 0),
                TileContentKind.Json => state.SelfCompleted && state.ChildrenDiscoveryDone && state.PendingChildBranches.Count == 0,
                _ => state.SelfCompleted
            };
        }

        private void TryQueueParentRemovalIfReady(PlannerState.TileLifecycle state)
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

            if (Output.DryRun)
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
            _state.Outbound.Enqueue(new RemoveSlotsCommand(state.StateId, state.TileId, state.SlotIds.ToList()));
        }

        private void PropagateCompletion(string stateId)
        {
            var queue = new Queue<string>();
            queue.Enqueue(stateId);

            while (queue.Count > 0)
            {
                string currentId = queue.Dequeue();
                if (!_state.TileStates.TryGetValue(currentId, out PlannerState.TileLifecycle? state))
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
                    _ = _state.DeferredGlbTiles.Remove(state.StateId);
                }

                if (string.IsNullOrWhiteSpace(state.ParentStateId))
                {
                    continue;
                }

                if (!_state.TileStates.TryGetValue(state.ParentStateId, out PlannerState.TileLifecycle? parentState))
                {
                    continue;
                }

                _ = parentState.PendingChildBranches.Remove(state.StateId);
                queue.Enqueue(parentState.StateId);
            }
        }

        private void MarkTileCompleted(string stateId)
        {
            if (!_state.TileStates.TryGetValue(stateId, out PlannerState.TileLifecycle? state))
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
                   _state.TileStates.TryGetValue(currentId, out PlannerState.TileLifecycle? current))
            {
                if (current.BranchHasVisibleContent)
                {
                    break;
                }

                current.BranchHasVisibleContent = true;
                currentId = current.ParentStateId;
            }
        }

        private bool TryQueueDeferredFallback(PlannerState.TileLifecycle state, string reason)
        {
            if (!state.DeferredSuppressed ||
                state.SelfCompleted ||
                state.FallbackQueued ||
                !state.ChildrenDiscoveryDone ||
                state.PendingChildBranches.Count > 0 ||
                state.BranchHasVisibleContent ||
                !_state.DeferredGlbTiles.TryGetValue(state.StateId, out TileSelectionResult? deferredTile))
            {
                return false;
            }

            if (_state.PendingTilesets.Count > 0)
            {
                return false;
            }

            _ = _state.DeferredGlbTiles.Remove(state.StateId);
            _state.PendingGlbTiles.Enqueue(deferredTile, GetTraversalPriority(deferredTile));
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
            if (_state.DeferredGlbTiles.Count == 0)
            {
                return;
            }

            foreach (string tileId in _state.DeferredGlbTiles.Keys.ToList())
            {
                if (!_state.TileStates.TryGetValue(tileId, out PlannerState.TileLifecycle? state))
                {
                    continue;
                }

                _ = TryQueueDeferredFallback(state, reason);
            }
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
            if (!_state.Initialized)
            {
                throw new InvalidOperationException("Planner is not initialized.");
            }
        }
    }
}
