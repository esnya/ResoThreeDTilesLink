using System.Numerics;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Pipeline;

public sealed class TileStreamingService
{
    // 3D Tiles glTF content is Y-up; convert to tiles/world Z-up before tile transform application.
    private static readonly Matrix4x4d GltfYUpToZUp = new(
        1d, 0d, 0d, 0d,
        0d, 0d, 1d, 0d,
        0d, -1d, 0d, 0d,
        0d, 0d, 0d, 1d);

    private readonly ITileContentFetcher _fetcher;
    private readonly ITileSelector _selector;
    private readonly IGlbMeshExtractor _glbMeshExtractor;
    private readonly ICoordinateTransformer _coordinateTransformer;
    private readonly IResoniteLinkClient _resoniteLinkClient;
    private readonly IGoogleAccessTokenProvider _googleAccessTokenProvider;
    private readonly ILogger<TileStreamingService> _logger;

    public TileStreamingService(
        ITileContentFetcher fetcher,
        ITileSelector selector,
        IGlbMeshExtractor glbMeshExtractor,
        ICoordinateTransformer coordinateTransformer,
        IResoniteLinkClient resoniteLinkClient,
        IGoogleAccessTokenProvider googleAccessTokenProvider,
        ILogger<TileStreamingService> logger)
    {
        _fetcher = fetcher;
        _selector = selector;
        _glbMeshExtractor = glbMeshExtractor;
        _coordinateTransformer = coordinateTransformer;
        _resoniteLinkClient = resoniteLinkClient;
        _googleAccessTokenProvider = googleAccessTokenProvider;
        _logger = logger;
    }

    public async Task<RunSummary> RunAsync(StreamerOptions options, CancellationToken cancellationToken)
    {
        var auth = await BuildAuthAsync(options, cancellationToken);

        _logger.LogInformation("Fetching root tileset from Google Map Tiles API.");
        var tileset = await _fetcher.FetchRootTilesetAsync(auth, cancellationToken);
        var attributionOrder = new List<string>();
        var knownAttributions = new HashSet<string>(StringComparer.Ordinal);
        var activeAttributionCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var streamedMeshes = 0;
        var failedTiles = 0;
        var processedTiles = 0;
        var candidateTiles = 0;
        var streamedTileCount = 0;
        var square = new QuerySquare(options.HalfWidthM);
        var tilesetCache = new Dictionary<string, Tileset>(StringComparer.OrdinalIgnoreCase);
        var tileStates = new Dictionary<string, TileLifecycle>(StringComparer.Ordinal);
        var queuedGlbTileIds = new HashSet<string>(StringComparer.Ordinal);
        var pendingTilesets = new PriorityQueue<PendingTileset, double>();
        pendingTilesets.Enqueue(new PendingTileset(tileset, Matrix4x4d.Identity, string.Empty, 0, null), 0d);
        var pendingGlbTiles = new PriorityQueue<TileSelectionResult, double>();
        var nestedTilesetFetches = 0;
        var maxNestedTilesetFetches = SMath.Max(options.MaxTiles * 64, 512);
        // Use unbounded subtree selection to avoid dropping branches in dense urban areas.
        var selectBudgetPerSubtree = 0;

        double GetTraversalPriority(TileSelectionResult tile)
        {
            var span = tile.HorizontalSpanM ?? 1_000_000_000d;
            var depth = tile.Depth;
            var leafBias = tile.HasChildren ? 0d : -0.1d;
            // Coarse coverage first: shallower depth first, then larger span first.
            return (depth * 1_000_000_000_000d) - span + leafBias;
        }

        static string? NormalizeAttributionOwner(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim();
        }

        static IReadOnlyList<string> ParseAttributionOwners(IEnumerable<string> values)
        {
            var owners = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var segments = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var segment in segments)
                {
                    var normalized = NormalizeAttributionOwner(segment);
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
            foreach (var normalized in ParseAttributionOwners(rawValues))
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

            var changed = false;
            foreach (var normalized in state.AttributionOwners)
            {
                if (activeAttributionCounts.TryGetValue(normalized, out var count))
                {
                    activeAttributionCounts[normalized] = count + 1;
                }
                else
                {
                    activeAttributionCounts[normalized] = 1;
                }

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

            var changed = false;
            foreach (var normalized in state.AttributionOwners)
            {
                if (!activeAttributionCounts.TryGetValue(normalized, out var count))
                {
                    continue;
                }

                if (count <= 1)
                {
                    activeAttributionCounts.Remove(normalized);
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
                cancellationToken);
        }

        TileLifecycle GetOrCreateTileState(string tileId, string? parentTileId = null, TileContentKind? contentKind = null)
        {
            if (!tileStates.TryGetValue(tileId, out var state))
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
            var tileState = GetOrCreateTileState(tile.TileId, tile.ParentTileId, tile.ContentKind);

            if (string.IsNullOrWhiteSpace(tile.ParentTileId))
            {
                return;
            }

            var parentState = GetOrCreateTileState(tile.ParentTileId);
            parentState.DirectChildren.Add(tile.TileId);
            if (tileState.BranchCompleted)
            {
                parentState.PendingChildBranches.Remove(tile.TileId);
            }
            else
            {
                parentState.PendingChildBranches.Add(tile.TileId);
            }
        }

        bool IsBranchCompleteForParent(TileLifecycle state)
        {
            return state.ContentKind switch
            {
                TileContentKind.Glb => state.SelfCompleted,
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

            var allRemoved = true;
            foreach (var slotId in state.SlotIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _resoniteLinkClient.RemoveSlotAsync(slotId, cancellationToken);
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
                await RefreshSessionLicenseCreditAsync();
            }
        }

        async Task PropagateCompletionAsync(string tileId)
        {
            var queue = new Queue<string>();
            queue.Enqueue(tileId);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                if (!tileStates.TryGetValue(currentId, out var state))
                {
                    continue;
                }

                await TryRemoveParentTileIfReadyAsync(state);

                if (state.BranchCompleted || !IsBranchCompleteForParent(state))
                {
                    continue;
                }

                state.BranchCompleted = true;
                if (string.IsNullOrWhiteSpace(state.ParentTileId))
                {
                    continue;
                }

                var parentState = GetOrCreateTileState(state.ParentTileId);
                parentState.PendingChildBranches.Remove(state.TileId);
                queue.Enqueue(parentState.TileId);
            }
        }

        async Task MarkTileCompletedAsync(string tileId)
        {
            if (!tileStates.TryGetValue(tileId, out var state))
            {
                return;
            }

            state.SelfCompleted = true;
            await PropagateCompletionAsync(tileId);
        }

        async Task<bool> TryStreamNextGlbAsync()
        {
            TileSelectionResult? glbTile = pendingGlbTiles.Count > 0 ? pendingGlbTiles.Dequeue() : null;

            if (glbTile is null)
            {
                return false;
            }

            var glbState = GetOrCreateTileState(glbTile.TileId, glbTile.ParentTileId, TileContentKind.Glb);
            var streamedSlotIds = new List<string>();
            try
            {
                candidateTiles++;
                var glb = await _fetcher.FetchTileContentAsync(glbTile.ContentUri, auth, cancellationToken);
                var extracted = _glbMeshExtractor.Extract(glb);
                var meshes = extracted.Meshes;
                glbState.AttributionOwners = ParseAttributionOwners(
                    string.IsNullOrWhiteSpace(extracted.AssetCopyright)
                        ? []
                        : [extracted.AssetCopyright!]);
                RegisterAttributionOrder(glbState.AttributionOwners);
                var texturedMeshes = meshes.Count(m => m.BaseColorTextureBytes is { Length: > 0 });

                foreach (var mesh in meshes)
                {
                    var payload = ToEunPayload(mesh, glbTile.WorldTransform, options.Reference, glbTile.TileId);
                    if (!options.DryRun)
                    {
                        var slotId = await _resoniteLinkClient.SendTileMeshAsync(payload, cancellationToken);
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
                    glbTile.HorizontalSpanM?.ToString("F1") ?? "n/a",
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

            foreach (var slotId in streamedSlotIds)
            {
                glbState.SlotIds.Add(slotId);
            }

            if (!options.DryRun && streamedSlotIds.Count > 0 && TryActivateAttributions(glbState))
            {
                await RefreshSessionLicenseCreditAsync();
            }

            await MarkTileCompletedAsync(glbTile.TileId);
            return true;
        }

        TileSelectionResult? PeekNextGlbTile()
        {
            if (pendingGlbTiles.TryPeek(out var nextTile, out _))
            {
                return nextTile;
            }

            return null;
        }

        if (!options.DryRun)
        {
            _logger.LogInformation("Connecting to Resonite Link at {Host}:{Port}", options.LinkHost, options.LinkPort);
            await _resoniteLinkClient.ConnectAsync(options.LinkHost, options.LinkPort, cancellationToken);
            await RefreshSessionLicenseCreditAsync();
        }

        try
        {
            while ((pendingTilesets.Count > 0 || pendingGlbTiles.Count > 0) &&
                   streamedTileCount < options.MaxTiles)
            {
                if (pendingTilesets.Count > 0 && nestedTilesetFetches < maxNestedTilesetFetches)
                {
                    var tilesetWork = pendingTilesets.Dequeue();

                    if (tilesetWork.DepthOffset > options.MaxDepth)
                    {
                        if (!string.IsNullOrWhiteSpace(tilesetWork.OwnerTileId))
                        {
                            var ownerState = GetOrCreateTileState(tilesetWork.OwnerTileId);
                            ownerState.SelfCompleted = true;
                            ownerState.ChildrenDiscoveryDone = true;
                            await PropagateCompletionAsync(ownerState.TileId);
                        }

                        continue;
                    }

                    var selected = _selector.Select(
                        tilesetWork.Tileset,
                        options.Reference,
                        square,
                        options.MaxDepth,
                        options.DetailTargetM,
                        selectBudgetPerSubtree,
                        tilesetWork.ParentWorld,
                        tilesetWork.IdPrefix,
                        tilesetWork.DepthOffset,
                        tilesetWork.OwnerTileId);

                    _logger.LogDebug("Selected {Count} candidate tiles from subtree '{Prefix}'.", selected.Count, tilesetWork.IdPrefix);

                    foreach (var tile in selected)
                    {
                        RegisterTile(tile);
                    }

                    foreach (var tile in selected
                                 .OrderBy(static t => t.ContentKind == TileContentKind.Json ? 0 : 1)
                                 .ThenBy(GetTraversalPriority))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var tileState = GetOrCreateTileState(tile.TileId, tile.ParentTileId, tile.ContentKind);

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
                                        await PropagateCompletionAsync(tile.TileId);
                                        _logger.LogInformation("Skipped nested tileset at max depth for tile {TileId}.", tile.TileId);
                                        continue;
                                    }

                                    tileState.ChildrenDiscoveryDone = false;
                                    try
                                    {
                                        if (!tilesetCache.TryGetValue(tile.ContentUri.AbsoluteUri, out var nestedTileset))
                                        {
                                            nestedTileset = await _fetcher.FetchTilesetAsync(tile.ContentUri, auth, cancellationToken);
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
                                        await PropagateCompletionAsync(tile.TileId);
                                        _logger.LogInformation("Skipped non-traversable JSON tile {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                                    }
                                    catch (Exception ex)
                                    {
                                        failedTiles++;
                                        tileState.ChildrenDiscoveryDone = true;
                                        await PropagateCompletionAsync(tile.TileId);
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

                                    pendingGlbTiles.Enqueue(tile, GetTraversalPriority(tile));

                                    continue;
                                }

                                default:
                                {
                                    processedTiles++;
                                    tileState.ChildrenDiscoveryDone = true;
                                    await MarkTileCompletedAsync(tile.TileId);
                                    _logger.LogInformation("Skipped unsupported tile content {TileId} ({Uri}).", tile.TileId, tile.ContentUri);
                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failedTiles++;
                            tileState.ChildrenDiscoveryDone = true;
                            await MarkTileCompletedAsync(tile.TileId);
                            _logger.LogWarning(ex, "Failed while discovering tile {TileId} from {Uri}", tile.TileId, tile.ContentUri);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(tilesetWork.OwnerTileId))
                    {
                        var owner = GetOrCreateTileState(tilesetWork.OwnerTileId);
                        owner.SelfCompleted = true;
                        owner.ChildrenDiscoveryDone = true;
                        await PropagateCompletionAsync(owner.TileId);
                    }
                }

                if (streamedTileCount >= options.MaxTiles)
                {
                    break;
                }

                if (pendingGlbTiles.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var nextGlbTile = PeekNextGlbTile();
                    if (nextGlbTile is not null &&
                        pendingTilesets.TryPeek(out var nextTileset, out _) &&
                        nestedTilesetFetches < maxNestedTilesetFetches &&
                        nextTileset.DepthOffset < nextGlbTile.Depth)
                    {
                        continue;
                    }

                    await TryStreamNextGlbAsync();
                }

                if (pendingTilesets.Count > 0 &&
                    nestedTilesetFetches >= maxNestedTilesetFetches &&
                    pendingGlbTiles.Count == 0)
                {
                    _logger.LogWarning(
                        "Stopped traversal because nested tileset fetch budget was reached ({MaxFetches}) and no streamable GLB tiles are queued.",
                        maxNestedTilesetFetches);
                    break;
                }
            }
        }
        finally
        {
            if (!options.DryRun)
            {
                await _resoniteLinkClient.DisconnectAsync(cancellationToken);
            }
        }

        if (streamedTileCount >= options.MaxTiles &&
            (pendingGlbTiles.Count > 0 || pendingTilesets.Count > 0))
        {
            _logger.LogWarning(
                "Stopped at max tile budget ({MaxTiles}) with pending work (glb={PendingGlb}, tilesets={PendingTilesets}). Increase --max-tiles to reduce holes.",
                options.MaxTiles,
                pendingGlbTiles.Count,
                pendingTilesets.Count);
        }

        return new RunSummary(candidateTiles, processedTiles, streamedMeshes, failedTiles);
    }

    private TileMeshPayload ToEunPayload(MeshData mesh, Matrix4x4d tileWorld, GeoReference reference, string tileId)
    {
        // 3D Tiles/glTF transform chain:
        // glTF node local (Y-up) -> tiles/world frame (Z-up) -> tile world transform.
        var meshWorld = (mesh.LocalTransform * GltfYUpToZUp) * tileWorld;
        var meshOriginEcef = meshWorld.TransformPoint(new Vector3d(0d, 0d, 0d));
        var meshOriginEun = ToEun(meshOriginEcef, reference);

        var basisXEun = ToEun(meshWorld.TransformPoint(new Vector3d(1d, 0d, 0d)), reference) - meshOriginEun;
        var basisYEun = ToEun(meshWorld.TransformPoint(new Vector3d(0d, 1d, 0d)), reference) - meshOriginEun;
        var basisZEun = ToEun(meshWorld.TransformPoint(new Vector3d(0d, 0d, 1d)), reference) - meshOriginEun;
        var (slotRotation, slotScale) = BuildSlotFrame(basisXEun, basisYEun, basisZEun);
        var invRotation = Quaternion.Inverse(slotRotation);

        var worldVertices = new List<Vector3d>(mesh.Vertices.Count);
        var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (var vertex in mesh.Vertices)
        {
            var worldEcef = meshWorld.TransformPoint(vertex);
            var worldEun = ToEun(worldEcef, reference);
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
        var maxAbsVertex = 0f;

        foreach (var worldEun in worldVertices)
        {
            var delta = worldEun - slotOriginEun;

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

        foreach (var uv in mesh.Uvs)
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
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Indices[i];
            var b = mesh.Indices[i + 1];
            var c = mesh.Indices[i + 2];
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
            mesh.BaseColorTextureExtension);
    }

    private static string BuildMeshSlotName(string tileId, string meshName)
    {
        var compactTileId = tileId.Replace("/", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(compactTileId))
        {
            compactTileId = "tile";
        }

        return $"tile_{compactTileId}_{meshName}";
    }

    private Vector3d ToEun(Vector3d ecef, GeoReference reference)
    {
        var enu = _coordinateTransformer.EcefToEnu(ecef, reference);
        return _coordinateTransformer.EnuToEun(enu);
    }

    private static (Quaternion Rotation, Vector3 Scale) BuildSlotFrame(Vector3d basisX, Vector3d basisY, Vector3d basisZ)
    {
        const double epsilon = 1e-9d;
        var sx = (float)SMath.Max(basisX.Length(), epsilon);
        var sy = (float)SMath.Max(basisY.Length(), epsilon);
        var sz = (float)SMath.Max(basisZ.Length(), epsilon);

        var x = NormalizeOrFallback(basisX, new Vector3d(1d, 0d, 0d));
        var yProjected = basisY - (Vector3d.Dot(basisY, x) * x);
        var y = NormalizeOrFallback(yProjected, new Vector3d(0d, 1d, 0d));
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
        if (!float.IsFinite(rotation.X) ||
            !float.IsFinite(rotation.Y) ||
            !float.IsFinite(rotation.Z) ||
            !float.IsFinite(rotation.W))
        {
            rotation = Quaternion.Identity;
        }
        else
        {
            rotation = Quaternion.Normalize(rotation);
        }

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
        for (var i = 0; i < attributionOrder.Count; i++)
        {
            orderIndex[attributionOrder[i]] = i;
        }

        foreach (var value in attributionOrder)
        {
            if (activeAttributionCounts.TryGetValue(value, out var count) && count > 0)
            {
                active.Add((value, count, orderIndex[value]));
            }
        }

        if (active.Count == 0)
        {
            return "Google Maps";
        }

        var ordered = active
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

        var token = await _googleAccessTokenProvider.GetAccessTokenAsync(cancellationToken);
        return new GoogleTilesAuth(null, token);
    }

    private sealed record PendingTileset(
        Tileset Tileset,
        Matrix4x4d ParentWorld,
        string IdPrefix,
        int DepthOffset,
        string? OwnerTileId);

    private sealed class TileLifecycle
    {
        public TileLifecycle(string tileId)
        {
            TileId = tileId;
        }

        public string TileId { get; }
        public string? ParentTileId { get; set; }
        public TileContentKind ContentKind { get; set; } = TileContentKind.Other;
        public bool SelfCompleted { get; set; }
        public bool BranchCompleted { get; set; }
        public bool ChildrenDiscoveryDone { get; set; } = true;
        public bool Removed { get; set; }
        public IReadOnlyList<string> AttributionOwners { get; set; } = Array.Empty<string>();
        public bool AttributionsApplied { get; set; }
        public HashSet<string> DirectChildren { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PendingChildBranches { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SlotIds { get; } = new(StringComparer.Ordinal);
    }
}
