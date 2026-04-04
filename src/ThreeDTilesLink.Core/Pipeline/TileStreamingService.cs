using System.Numerics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class TileStreamingService(
        ITileContentFetcher fetcher,
        ITileSelector selector,
        IGlbMeshExtractor glbMeshExtractor,
        ICoordinateTransformer coordinateTransformer,
        IResoniteLinkClient resoniteLinkClient,
        IGoogleAccessTokenProvider googleAccessTokenProvider,
        ILogger<TileStreamingService> logger)
    {
        // 3D Tiles glTF content is Y-up; convert to tiles/world Z-up before tile transform application.
        private static readonly Matrix4x4d GltfYUpToZUp = new(
            1d, 0d, 0d, 0d,
            0d, 0d, 1d, 0d,
            0d, -1d, 0d, 0d,
            0d, 0d, 0d, 1d);

        private readonly ITileContentFetcher _fetcher = fetcher;
        private readonly ITileSelector _selector = selector;
        private readonly IGlbMeshExtractor _glbMeshExtractor = glbMeshExtractor;
        private readonly ICoordinateTransformer _coordinateTransformer = coordinateTransformer;
        private readonly IResoniteLinkClient _resoniteLinkClient = resoniteLinkClient;
        private readonly IGoogleAccessTokenProvider _googleAccessTokenProvider = googleAccessTokenProvider;
        private readonly ILogger<TileStreamingService> _logger = logger;

        public async Task<RunSummary> RunAsync(StreamerOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            GoogleTilesAuth auth = await BuildAuthAsync(options, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Fetching root tileset from Google Map Tiles API.");
            Tileset tileset = await _fetcher.FetchRootTilesetAsync(auth, cancellationToken).ConfigureAwait(false);
            var attributionOrder = new List<string>();
            var knownAttributions = new HashSet<string>(StringComparer.Ordinal);
            var activeAttributionCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            int streamedMeshes = 0;
            int failedTiles = 0;
            int processedTiles = 0;
            int candidateTiles = 0;
            int streamedTileCount = 0;
            var square = new QuerySquare(options.HalfWidthM);
            var tilesetCache = new Dictionary<string, Tileset>(StringComparer.OrdinalIgnoreCase);
            var tileStates = new Dictionary<string, TileLifecycle>(StringComparer.Ordinal);
            var queuedGlbTileIds = new HashSet<string>(StringComparer.Ordinal);
            var pendingTilesets = new PriorityQueue<PendingTileset, double>();
            pendingTilesets.Enqueue(new PendingTileset(tileset, Matrix4x4d.Identity, string.Empty, 0, null), 0d);
            var pendingGlbTiles = new PriorityQueue<TileSelectionResult, double>();
            var deferredGlbTiles = new Dictionary<string, TileSelectionResult>(StringComparer.Ordinal);
            int nestedTilesetFetches = 0;
            int maxNestedTilesetFetches = SMath.Max(options.MaxTiles * 64, 512);
            // Use unbounded subtree selection to avoid dropping branches in dense urban areas.
            int selectBudgetPerSubtree = 0;
            double renderStartSpanRatio = options.RenderStartSpanRatio > 0d ? options.RenderStartSpanRatio : 4d;
            double renderStartSpanM = options.HalfWidthM * renderStartSpanRatio;
            bool bootstrapActive = true;

            _logger.LogInformation(
                "Bootstrap discovery active (renderStartSpan={RenderStartSpan}m, halfWidth={HalfWidth}m, ratio={Ratio}).",
                renderStartSpanM.ToString("F1", CultureInfo.InvariantCulture),
                options.HalfWidthM.ToString("F1", CultureInfo.InvariantCulture),
                renderStartSpanRatio.ToString("F2", CultureInfo.InvariantCulture));

            double GetTraversalPriority(TileSelectionResult tile)
            {
                double span = tile.HorizontalSpanM ?? 1_000_000_000d;
                int depth = tile.Depth;
                double leafBias = tile.HasChildren ? 0d : -0.1d;
                // Coarse coverage first: shallower depth first, then larger span first.
                return (depth * 1_000_000_000_000d) - span + leafBias;
            }

            bool IsStreamableGlbCandidate(TileSelectionResult tile)
            {
                return tile.HorizontalSpanM is null ||
                    tile.HorizontalSpanM.Value <= renderStartSpanM ||
                    !tile.HasChildren;
            }

            void TryDeactivateBootstrap(string reason, TileSelectionResult? triggerTile = null)
            {
                if (!bootstrapActive || pendingGlbTiles.Count == 0)
                {
                    return;
                }

                bootstrapActive = false;
                _logger.LogInformation(
                    "Bootstrap discovery complete: reason={Reason}, streamable={Streamable}, deferred={Deferred}, pendingTilesets={PendingTilesets}, triggerTile={TileId}, triggerDepth={Depth}, triggerSpan={Span}m.",
                    reason,
                    pendingGlbTiles.Count,
                    deferredGlbTiles.Count,
                    pendingTilesets.Count,
                    triggerTile?.TileId ?? "n/a",
                    triggerTile?.Depth.ToString(CultureInfo.InvariantCulture) ?? "n/a",
                    triggerTile?.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a");
            }

            static string? NormalizeAttributionOwner(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            static IReadOnlyList<string> ParseAttributionOwners(IEnumerable<string> values)
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

            void RegisterAttributionOrder(IEnumerable<string> rawValues)
            {
                foreach (string normalized in ParseAttributionOwners(rawValues))
                {
                    if (knownAttributions.Add(normalized))
                    {
                        attributionOrder.Add(normalized);
                    }
                }
            }

            bool TryActivateAttributions(TileLifecycle state)
            {
                if (state.AttributionsApplied)
                {
                    return false;
                }

                bool changed = false;
                foreach (string normalized in state.AttributionOwners)
                {
                    activeAttributionCounts[normalized] = activeAttributionCounts.TryGetValue(normalized, out int count) ? count + 1 : 1;

                    changed = true;
                }

                state.AttributionsApplied = true;
                return changed;
            }

            bool TryDeactivateAttributions(TileLifecycle state)
            {
                if (!state.AttributionsApplied)
                {
                    return false;
                }

                bool changed = false;
                foreach (string normalized in state.AttributionOwners)
                {
                    if (!activeAttributionCounts.TryGetValue(normalized, out int count))
                    {
                        continue;
                    }

                    if (count <= 1)
                    {
                        _ = activeAttributionCounts.Remove(normalized);
                    }
                    else
                    {
                        activeAttributionCounts[normalized] = count - 1;
                    }

                    changed = true;
                }

                state.AttributionsApplied = false;
                return changed;
            }

            async Task RefreshSessionLicenseCreditAsync()
            {
                if (options.DryRun)
                {
                    return;
                }

                await _resoniteLinkClient.SetSessionLicenseCreditAsync(
                    BuildLicenseCreditString(attributionOrder, activeAttributionCounts),
                    cancellationToken).ConfigureAwait(false);
            }

            TileLifecycle GetOrCreateTileState(string tileId, string? parentTileId = null, TileContentKind? contentKind = null)
            {
                if (!tileStates.TryGetValue(tileId, out TileLifecycle? state))
                {
                    state = new TileLifecycle(tileId)
                    {
                        ParentTileId = parentTileId
                    };
                    if (contentKind.HasValue)
                    {
                        state.ContentKind = contentKind.Value;
                    }

                    tileStates[tileId] = state;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(parentTileId) && string.IsNullOrWhiteSpace(state.ParentTileId))
                    {
                        state.ParentTileId = parentTileId;
                    }

                    if (contentKind.HasValue)
                    {
                        state.ContentKind = contentKind.Value;
                    }
                }

                return state;
            }

            void RegisterTile(TileSelectionResult tile)
            {
                TileLifecycle tileState = GetOrCreateTileState(tile.TileId, tile.ParentTileId, tile.ContentKind);

                if (string.IsNullOrWhiteSpace(tile.ParentTileId))
                {
                    return;
                }

                TileLifecycle parentState = GetOrCreateTileState(tile.ParentTileId);
                _ = parentState.DirectChildren.Add(tile.TileId);
                _ = tileState.BranchCompleted
                    ? parentState.PendingChildBranches.Remove(tile.TileId)
                    : parentState.PendingChildBranches.Add(tile.TileId);
            }

            bool IsBranchCompleteForParent(TileLifecycle state)
            {
                return state.ContentKind switch
                {
                    TileContentKind.Glb => state.SelfCompleted || (state.DeferredSuppressed && state.ChildrenDiscoveryDone && state.PendingChildBranches.Count == 0),
                    TileContentKind.Json => state.SelfCompleted && state.ChildrenDiscoveryDone && state.PendingChildBranches.Count == 0,
                    _ => state.SelfCompleted
                };
            }

            async Task TryRemoveParentTileIfReadyAsync(TileLifecycle state)
            {
                if (state.Removed ||
                    state.ContentKind != TileContentKind.Glb ||
                    !state.SelfCompleted ||
                    !state.ChildrenDiscoveryDone ||
                    state.DirectChildren.Count == 0 ||
                    state.PendingChildBranches.Count > 0)
                {
                    return;
                }

                if (options.DryRun)
                {
                    state.Removed = true;
                    return;
                }

                bool allRemoved = true;
                foreach (string slotId in state.SlotIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await _resoniteLinkClient.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        allRemoved = false;
                        failedTiles++;
                        _logger.LogWarning(ex, "Failed to remove parent tile slot {SlotId} for tile {TileId}.", slotId, state.TileId);
                    }
                }

                if (!allRemoved)
                {
                    return;
                }

                state.Removed = true;
                if (TryDeactivateAttributions(state))
                {
                    await RefreshSessionLicenseCreditAsync().ConfigureAwait(false);
                }
            }

            async Task PropagateCompletionAsync(string tileId)
            {
                var queue = new Queue<string>();
                queue.Enqueue(tileId);

                while (queue.Count > 0)
                {
                    string currentId = queue.Dequeue();
                    if (!tileStates.TryGetValue(currentId, out TileLifecycle? state))
                    {
                        continue;
                    }

                    if (TryQueueDeferredFallback(state, "branch-complete"))
                    {
                        continue;
                    }

                    await TryRemoveParentTileIfReadyAsync(state).ConfigureAwait(false);

                    if (state.BranchCompleted || !IsBranchCompleteForParent(state))
                    {
                        continue;
                    }

                    state.BranchCompleted = true;
                    if (state.DeferredSuppressed && !state.SelfCompleted)
                    {
                        _ = deferredGlbTiles.Remove(state.TileId);
                    }

                    if (string.IsNullOrWhiteSpace(state.ParentTileId))
                    {
                        continue;
                    }

                    TileLifecycle parentState = GetOrCreateTileState(state.ParentTileId);
                    _ = parentState.PendingChildBranches.Remove(state.TileId);
                    queue.Enqueue(parentState.TileId);
                }
            }

            async Task MarkTileCompletedAsync(string tileId)
            {
                if (!tileStates.TryGetValue(tileId, out TileLifecycle? state))
                {
                    return;
                }

                state.SelfCompleted = true;
                await PropagateCompletionAsync(tileId).ConfigureAwait(false);
            }

            void MarkBranchVisible(string tileId)
            {
                string? currentId = tileId;
                while (!string.IsNullOrWhiteSpace(currentId) &&
                       tileStates.TryGetValue(currentId, out TileLifecycle? current))
                {
                    if (current.BranchHasVisibleContent)
                    {
                        break;
                    }

                    current.BranchHasVisibleContent = true;
                    currentId = current.ParentTileId;
                }
            }

            bool TryQueueDeferredFallback(TileLifecycle state, string reason)
            {
                if (!state.DeferredSuppressed ||
                    state.SelfCompleted ||
                    state.FallbackQueued ||
                    !state.ChildrenDiscoveryDone ||
                    state.PendingChildBranches.Count > 0 ||
                    state.BranchHasVisibleContent ||
                    !deferredGlbTiles.TryGetValue(state.TileId, out TileSelectionResult? deferredTile))
                {
                    return false;
                }

                if (pendingTilesets.Count > 0)
                {
                    return false;
                }

                _ = deferredGlbTiles.Remove(state.TileId);
                pendingGlbTiles.Enqueue(deferredTile, GetTraversalPriority(deferredTile));
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

            void QueueReadyDeferredFallbacks(string reason)
            {
                if (deferredGlbTiles.Count == 0)
                {
                    return;
                }

                foreach (string tileId in deferredGlbTiles.Keys.ToList())
                {
                    if (!tileStates.TryGetValue(tileId, out TileLifecycle? state))
                    {
                        continue;
                    }

                    _ = TryQueueDeferredFallback(state, reason);
                }
            }

            async Task<bool> TryStreamNextGlbAsync()
            {
                TileSelectionResult? glbTile = pendingGlbTiles.Count > 0 ? pendingGlbTiles.Dequeue() : null;

                if (glbTile is null)
                {
                    return false;
                }

                TileLifecycle glbState = GetOrCreateTileState(glbTile.TileId, glbTile.ParentTileId, TileContentKind.Glb);
                var streamedSlotIds = new List<string>();
                try
                {
                    candidateTiles++;
                    byte[] glb = await _fetcher.FetchTileContentAsync(glbTile.ContentUri, auth, cancellationToken).ConfigureAwait(false);
                    GlbExtractResult extracted = _glbMeshExtractor.Extract(glb);
                    IReadOnlyList<MeshData> meshes = extracted.Meshes;
                    glbState.AttributionOwners = ParseAttributionOwners(
                        string.IsNullOrWhiteSpace(extracted.AssetCopyright)
                            ? []
                            : [extracted.AssetCopyright!]);
                    RegisterAttributionOrder(glbState.AttributionOwners);
                    int texturedMeshes = meshes.Count(m => m.BaseColorTextureBytes is { Length: > 0 });

                    foreach (MeshData mesh in meshes)
                    {
                        TileMeshPayload payload = ToEunPayload(
                            mesh,
                            glbTile.WorldTransform,
                            options.Reference,
                            glbTile.TileId,
                            options.MeshParentSlotId);
                        if (!options.DryRun)
                        {
                            string? slotId = await _resoniteLinkClient.SendTileMeshAsync(payload, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(slotId))
                            {
                                streamedSlotIds.Add(slotId);
                            }
                        }

                        streamedMeshes++;
                    }

                    streamedTileCount++;
                    processedTiles++;
                    _logger.LogInformation(
                        "Processed tile {TileId} with {MeshCount} meshes ({TexturedMeshCount} textured) (span={Span}m depth={Depth}).",
                        glbTile.TileId,
                        meshes.Count,
                        texturedMeshes,
                        glbTile.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a",
                        glbTile.Depth);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    processedTiles++;
                    _logger.LogInformation("Skipped unavailable tile content {TileId} ({Uri}).", glbTile.TileId, glbTile.ContentUri);
                }
                catch (Exception ex)
                {
                    failedTiles++;
                    _logger.LogWarning(ex, "Failed to process tile {TileId} from {Uri}", glbTile.TileId, glbTile.ContentUri);
                }

                foreach (string slotId in streamedSlotIds)
                {
                    _ = glbState.SlotIds.Add(slotId);
                }

                if (streamedSlotIds.Count > 0)
                {
                    MarkBranchVisible(glbTile.TileId);
                }

                if (!options.DryRun && streamedSlotIds.Count > 0 && TryActivateAttributions(glbState))
                {
                    await RefreshSessionLicenseCreditAsync().ConfigureAwait(false);
                }

                await MarkTileCompletedAsync(glbTile.TileId).ConfigureAwait(false);
                return true;
            }

            if (!options.DryRun && options.ManageResoniteConnection)
            {
                _logger.LogInformation("Connecting to Resonite Link at {Host}:{Port}", options.LinkHost, options.LinkPort);
                await _resoniteLinkClient.ConnectAsync(options.LinkHost, options.LinkPort, cancellationToken).ConfigureAwait(false);
            }

            if (!options.DryRun)
            {
                await RefreshSessionLicenseCreditAsync().ConfigureAwait(false);
            }

            try
            {
                while ((pendingTilesets.Count > 0 || pendingGlbTiles.Count > 0 || deferredGlbTiles.Count > 0) &&
                       streamedTileCount < options.MaxTiles)
                {
                    QueueReadyDeferredFallbacks("loop-check");
                    _logger.LogDebug(
                        "Bootstrap={Bootstrap} queues: streamable={Streamable}, deferred={Deferred}, pendingTilesets={PendingTilesets}.",
                        bootstrapActive ? "active" : "inactive",
                        pendingGlbTiles.Count,
                        deferredGlbTiles.Count,
                        pendingTilesets.Count);

                    bool didWork = false;
                    if (!bootstrapActive && pendingGlbTiles.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _ = await TryStreamNextGlbAsync().ConfigureAwait(false);
                        didWork = true;
                    }

                    if (streamedTileCount >= options.MaxTiles)
                    {
                        break;
                    }

                    if (pendingTilesets.Count > 0 && nestedTilesetFetches < maxNestedTilesetFetches)
                    {
                        didWork = true;
                        PendingTileset tilesetWork = pendingTilesets.Dequeue();

                        if (tilesetWork.DepthOffset > options.MaxDepth)
                        {
                            if (!string.IsNullOrWhiteSpace(tilesetWork.OwnerTileId))
                            {
                                TileLifecycle ownerState = GetOrCreateTileState(tilesetWork.OwnerTileId);
                                ownerState.SelfCompleted = true;
                                ownerState.ChildrenDiscoveryDone = true;
                                await PropagateCompletionAsync(ownerState.TileId).ConfigureAwait(false);
                            }

                            continue;
                        }

                        double effectiveDetailTargetM = bootstrapActive
                            ? SMath.Max(options.DetailTargetM, renderStartSpanM)
                            : options.DetailTargetM;

                        IReadOnlyList<TileSelectionResult> selected = _selector.Select(
                            tilesetWork.Tileset,
                            options.Reference,
                            square,
                            options.MaxDepth,
                            effectiveDetailTargetM,
                            selectBudgetPerSubtree,
                            tilesetWork.ParentWorld,
                            tilesetWork.IdPrefix,
                            tilesetWork.DepthOffset,
                            tilesetWork.OwnerTileId);

                        _logger.LogDebug(
                            "Selected {Count} candidate tiles from subtree '{Prefix}' (detailTarget={DetailTarget}m, bootstrap={Bootstrap}).",
                            selected.Count,
                            tilesetWork.IdPrefix,
                            effectiveDetailTargetM.ToString("F1", CultureInfo.InvariantCulture),
                            bootstrapActive ? "active" : "inactive");

                        foreach (TileSelectionResult tile in selected)
                        {
                            RegisterTile(tile);
                        }

                        foreach (TileSelectionResult? tile in selected
                                     .OrderBy(static t => t.ContentKind == TileContentKind.Json ? 0 : 1)
                                     .ThenBy(GetTraversalPriority))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            TileLifecycle tileState = GetOrCreateTileState(tile.TileId, tile.ParentTileId, tile.ContentKind);

                            try
                            {
                                switch (tile.ContentKind)
                                {
                                    case TileContentKind.Json:
                                        {
                                            tileState.SelfCompleted = true;

                                            if (tile.Depth >= options.MaxDepth)
                                            {
                                                processedTiles++;
                                                tileState.ChildrenDiscoveryDone = true;
                                                await PropagateCompletionAsync(tile.TileId).ConfigureAwait(false);
                                                _logger.LogInformation("Skipped nested tileset at max depth for tile {TileId}.", tile.TileId);
                                                continue;
                                            }

                                            tileState.ChildrenDiscoveryDone = false;
                                            try
                                            {
                                                if (!tilesetCache.TryGetValue(tile.ContentUri.AbsoluteUri, out Tileset? nestedTileset))
                                                {
                                                    nestedTileset = await _fetcher.FetchTilesetAsync(tile.ContentUri, auth, cancellationToken).ConfigureAwait(false);
                                                    tilesetCache[tile.ContentUri.AbsoluteUri] = nestedTileset;
                                                    nestedTilesetFetches++;
                                                }

                                                var pending = new PendingTileset(
                                                    nestedTileset,
                                                    tile.WorldTransform,
                                                    tile.TileId,
                                                    tile.Depth + 1,
                                                    tile.TileId);

                                                pendingTilesets.Enqueue(pending, GetTraversalPriority(tile));
                                            }
                                            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
                                            {
                                                tileState.ChildrenDiscoveryDone = true;
                                                await PropagateCompletionAsync(tile.TileId).ConfigureAwait(false);
                                                _logger.LogInformation("Skipped non-traversable JSON tile {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                                            }
                                            catch (Exception ex)
                                            {
                                                failedTiles++;
                                                tileState.ChildrenDiscoveryDone = true;
                                                await PropagateCompletionAsync(tile.TileId).ConfigureAwait(false);
                                                _logger.LogWarning(ex, "Failed while traversing JSON tile {TileId} from {Uri}", tile.TileId, tile.ContentUri);
                                            }

                                            processedTiles++;
                                            continue;
                                        }

                                    case TileContentKind.Glb:
                                        {
                                            if (!queuedGlbTileIds.Add(tile.TileId))
                                            {
                                                continue;
                                            }

                                            if (IsStreamableGlbCandidate(tile))
                                            {
                                                tileState.DeferredSuppressed = false;
                                                tileState.FallbackQueued = false;
                                                _ = deferredGlbTiles.Remove(tile.TileId);
                                                pendingGlbTiles.Enqueue(tile, GetTraversalPriority(tile));
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
                                                deferredGlbTiles[tile.TileId] = tile;
                                                _logger.LogDebug(
                                                    "Deferred coarse GLB tile {TileId} (depth={Depth}, span={Span}m, threshold={Threshold}m).",
                                                    tile.TileId,
                                                    tile.Depth,
                                                    tile.HorizontalSpanM?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a",
                                                    renderStartSpanM.ToString("F1", CultureInfo.InvariantCulture));
                                            }

                                            continue;
                                        }

                                    case TileContentKind.Other:
                                        break;
                                    default:
                                        {
                                            processedTiles++;
                                            tileState.ChildrenDiscoveryDone = true;
                                            await MarkTileCompletedAsync(tile.TileId).ConfigureAwait(false);
                                            _logger.LogInformation("Skipped unsupported tile content {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                                            continue;
                                        }
                                }
                            }
                            catch (Exception ex)
                            {
                                failedTiles++;
                                tileState.ChildrenDiscoveryDone = true;
                                await MarkTileCompletedAsync(tile.TileId).ConfigureAwait(false);
                                _logger.LogWarning(ex, "Failed while discovering tile {TileId} from {Uri}", tile.TileId, tile.ContentUri);
                            }
                        }

                        QueueReadyDeferredFallbacks("subtree-processed");

                        if (!string.IsNullOrWhiteSpace(tilesetWork.OwnerTileId))
                        {
                            TileLifecycle owner = GetOrCreateTileState(tilesetWork.OwnerTileId);
                            owner.SelfCompleted = true;
                            owner.ChildrenDiscoveryDone = true;
                            await PropagateCompletionAsync(owner.TileId).ConfigureAwait(false);
                            QueueReadyDeferredFallbacks("owner-completed");
                        }
                    }

                    if (pendingTilesets.Count > 0 &&
                        nestedTilesetFetches >= maxNestedTilesetFetches &&
                        pendingGlbTiles.Count == 0 &&
                        deferredGlbTiles.Count == 0)
                    {
                        _logger.LogWarning(
                            "Stopped traversal because nested tileset fetch budget was reached ({MaxFetches}) and no streamable/deferred GLB tiles are queued.",
                            maxNestedTilesetFetches);
                        break;
                    }

                    if (!didWork)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (!options.DryRun && options.ManageResoniteConnection)
                {
                    await _resoniteLinkClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (streamedTileCount >= options.MaxTiles &&
                (pendingGlbTiles.Count > 0 || pendingTilesets.Count > 0 || deferredGlbTiles.Count > 0))
            {
                _logger.LogWarning(
                    "Stopped at max tile budget ({MaxTiles}) with pending work (streamableGlb={PendingGlb}, deferredGlb={PendingDeferredGlb}, tilesets={PendingTilesets}). Increase --max-tiles to reduce holes.",
                    options.MaxTiles,
                    pendingGlbTiles.Count,
                    deferredGlbTiles.Count,
                    pendingTilesets.Count);
            }

            return new RunSummary(candidateTiles, processedTiles, streamedMeshes, failedTiles);
        }

        private TileMeshPayload ToEunPayload(
            MeshData mesh,
            Matrix4x4d tileWorld,
            GeoReference reference,
            string tileId,
            string? parentSlotId)
        {
            // 3D Tiles/glTF transform chain:
            // glTF node local (Y-up) -> tiles/world frame (Z-up) -> tile world transform.
            Matrix4x4d meshWorld = mesh.LocalTransform * GltfYUpToZUp * tileWorld;
            Vector3d meshOriginEcef = meshWorld.TransformPoint(new Vector3d(0d, 0d, 0d));
            Vector3d meshOriginEun = ToEun(meshOriginEcef, reference);

            Vector3d basisXEun = ToEun(meshWorld.TransformPoint(new Vector3d(1d, 0d, 0d)), reference) - meshOriginEun;
            Vector3d basisYEun = ToEun(meshWorld.TransformPoint(new Vector3d(0d, 1d, 0d)), reference) - meshOriginEun;
            Vector3d basisZEun = ToEun(meshWorld.TransformPoint(new Vector3d(0d, 0d, 1d)), reference) - meshOriginEun;
            (Quaternion slotRotation, Vector3 slotScale) = BuildSlotFrame(basisXEun, basisYEun, basisZEun);
            var invRotation = Quaternion.Inverse(slotRotation);

            var worldVertices = new List<Vector3d>(mesh.Vertices.Count);
            var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            var max = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
            foreach (Vector3d vertex in mesh.Vertices)
            {
                Vector3d worldEcef = meshWorld.TransformPoint(vertex);
                Vector3d worldEun = ToEun(worldEcef, reference);
                worldVertices.Add(worldEun);

                min = new Vector3d(
                    SMath.Min(min.X, worldEun.X),
                    SMath.Min(min.Y, worldEun.Y),
                    SMath.Min(min.Z, worldEun.Z));
                max = new Vector3d(
                    SMath.Max(max.X, worldEun.X),
                    SMath.Max(max.Y, worldEun.Y),
                    SMath.Max(max.Z, worldEun.Z));
            }

            // Use mesh center as slot origin to keep vertex values compact and improve float precision.
            var slotOriginEun = new Vector3d(
                (min.X + max.X) * 0.5d,
                (min.Y + max.Y) * 0.5d,
                (min.Z + max.Z) * 0.5d);

            var vertices = new List<Vector3>(worldVertices.Count);
            var uvs = new List<Vector2>(mesh.Uvs.Count);
            float maxAbsVertex = 0f;

            foreach (Vector3d worldEun in worldVertices)
            {
                Vector3d delta = worldEun - slotOriginEun;

                var localRotated = Vector3.Transform(
                    new Vector3((float)delta.X, (float)delta.Y, (float)delta.Z),
                    invRotation);

                var local = new Vector3(
                    slotScale.X > 1e-6f ? localRotated.X / slotScale.X : localRotated.X,
                    slotScale.Y > 1e-6f ? localRotated.Y / slotScale.Y : localRotated.Y,
                    slotScale.Z > 1e-6f ? localRotated.Z / slotScale.Z : localRotated.Z);
                vertices.Add(local);
                maxAbsVertex = SMath.Max(maxAbsVertex, SMath.Max(SMath.Abs(local.X), SMath.Max(SMath.Abs(local.Y), SMath.Abs(local.Z))));
            }

            foreach (Vector2d uv in mesh.Uvs)
            {
                uvs.Add(new Vector2((float)uv.X, (float)uv.Y));
            }

            _logger.LogDebug(
                "Tile {TileId} mesh {MeshName}: slotPos=({PosX:F2},{PosY:F2},{PosZ:F2}) scale=({ScaleX:F4},{ScaleY:F4},{ScaleZ:F4}) localMaxAbs={LocalMaxAbs:F2}m",
                tileId,
                mesh.Name,
                slotOriginEun.X,
                slotOriginEun.Y,
                slotOriginEun.Z,
                slotScale.X,
                slotScale.Y,
                slotScale.Z,
                maxAbsVertex);

            // ENU -> EUN axis swap flips handedness. Reverse winding to keep front faces.
            var eunIndices = new List<int>(mesh.Indices.Count);
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int a = mesh.Indices[i];
                int b = mesh.Indices[i + 1];
                int c = mesh.Indices[i + 2];
                eunIndices.Add(a);
                eunIndices.Add(c);
                eunIndices.Add(b);
            }

            return new TileMeshPayload(
                BuildMeshSlotName(tileId, mesh.Name),
                vertices,
                eunIndices,
                uvs,
                mesh.HasUv0,
                new Vector3((float)slotOriginEun.X, (float)slotOriginEun.Y, (float)slotOriginEun.Z),
                slotRotation,
                slotScale,
                mesh.BaseColorTextureBytes,
                mesh.BaseColorTextureExtension,
                parentSlotId);
        }

        private static string BuildMeshSlotName(string tileId, string meshName)
        {
            string compactTileId = tileId.Replace("/", string.Empty, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(compactTileId))
            {
                compactTileId = "tile";
            }

            return $"tile_{compactTileId}_{meshName}";
        }

        private Vector3d ToEun(Vector3d ecef, GeoReference reference)
        {
            Vector3d enu = _coordinateTransformer.EcefToEnu(ecef, reference);
            return _coordinateTransformer.EnuToEun(enu);
        }

        private static (Quaternion Rotation, Vector3 Scale) BuildSlotFrame(Vector3d basisX, Vector3d basisY, Vector3d basisZ)
        {
            const double epsilon = 1e-9d;
            float sx = (float)SMath.Max(basisX.Length(), epsilon);
            float sy = (float)SMath.Max(basisY.Length(), epsilon);
            float sz = (float)SMath.Max(basisZ.Length(), epsilon);

            Vector3d x = NormalizeOrFallback(basisX, new Vector3d(1d, 0d, 0d));
            Vector3d yProjected = basisY - (Vector3d.Dot(basisY, x) * x);
            Vector3d y = NormalizeOrFallback(yProjected, new Vector3d(0d, 1d, 0d));
            var z = Vector3d.Normalize(Vector3d.Cross(x, y));

            if (z.Length() <= epsilon)
            {
                z = NormalizeOrFallback(basisZ, new Vector3d(0d, 0d, 1d));
                y = NormalizeOrFallback(Vector3d.Cross(z, x), new Vector3d(0d, 1d, 0d));
                z = NormalizeOrFallback(Vector3d.Cross(x, y), new Vector3d(0d, 0d, 1d));
            }

            if (Vector3d.Dot(z, basisZ) < 0d)
            {
                y = -1d * y;
                z = -1d * z;
            }

            var rotationMatrix = new Matrix4x4(
                (float)x.X, (float)x.Y, (float)x.Z, 0f,
                (float)y.X, (float)y.Y, (float)y.Z, 0f,
                (float)z.X, (float)z.Y, (float)z.Z, 0f,
                0f, 0f, 0f, 1f);

            var rotation = Quaternion.CreateFromRotationMatrix(rotationMatrix);
            rotation = !float.IsFinite(rotation.X) ||
                !float.IsFinite(rotation.Y) ||
                !float.IsFinite(rotation.Z) ||
                !float.IsFinite(rotation.W)
                ? Quaternion.Identity
                : Quaternion.Normalize(rotation);

            return (rotation, new Vector3(sx, sy, sz));
        }

        private static Vector3d NormalizeOrFallback(Vector3d value, Vector3d fallback)
        {
            var normalized = Vector3d.Normalize(value);
            return normalized.Length() <= 1e-9d ? fallback : normalized;
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

        private async Task<GoogleTilesAuth> BuildAuthAsync(StreamerOptions options, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                return new GoogleTilesAuth(options.ApiKey, null);
            }

            string token = await _googleAccessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return new GoogleTilesAuth(null, token);
        }

        private sealed record PendingTileset(
            Tileset Tileset,
            Matrix4x4d ParentWorld,
            string IdPrefix,
            int DepthOffset,
            string? OwnerTileId);

        private sealed class TileLifecycle(string tileId)
        {
            public string TileId { get; } = tileId;
            public string? ParentTileId { get; set; }
            public TileContentKind ContentKind { get; set; } = TileContentKind.Other;
            public bool SelfCompleted { get; set; }
            public bool BranchCompleted { get; set; }
            public bool ChildrenDiscoveryDone { get; set; } = true;
            public bool Removed { get; set; }
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
