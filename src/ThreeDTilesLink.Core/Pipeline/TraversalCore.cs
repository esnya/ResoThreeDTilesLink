using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

#pragma warning disable CA1822

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TraversalCore(ITileSelector selector)
    {
        private readonly ITileSelector _selector = selector;

        internal DiscoveryFacts Initialize(
            Tileset rootTileset,
            TileRunRequest request,
            InteractiveRunInput? interactive)
        {
            ArgumentNullException.ThrowIfNull(rootTileset);
            ArgumentNullException.ThrowIfNull(request);

            var facts = new DiscoveryFacts(
                request,
                interactive?.Checkpoint?.TilesetCache,
                interactive?.RemoveOutOfRangeTiles ?? false);
            ExpandDiscoveredTree(
                facts,
                rootTileset,
                Matrix4x4d.Identity,
                string.Empty,
                depthOffset: 0,
                parentContentTileId: null,
                parentContentStableId: null);
            return facts;
        }

        internal DesiredView ComputeDesiredView(DiscoveryFacts facts, SelectionState selectionState)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(selectionState);

            PlanningTree tree = BuildPlanningTree(facts, selectionState);
            HashSet<string> candidateStableIds = GetCandidateStableIds(tree);
            var desired = new HashSet<string>(StringComparer.Ordinal);
            if (tree.PlanningVisibleStableIds.Count == 0)
            {
                PopulateBootstrapDesired(tree, selectionState, candidateStableIds, desired);
            }
            else
            {
                PopulateReplacementDesired(tree, selectionState, candidateStableIds, desired);
            }

            return new DesiredView(desired, tree.SelectedStableIds, candidateStableIds);
        }

        internal List<DiscoveryWorkItem> PlanDiscovery(
            DiscoveryFacts facts,
            SelectionState selectionState,
            int availableSlots)
        {
            return PlanDiscovery(facts, selectionState, availableSlots, availableSlots);
        }

        internal List<DiscoveryWorkItem> PlanDiscovery(
            DiscoveryFacts facts,
            SelectionState selectionState,
            int availableNestedSlots,
            int availablePrepareSlots)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(selectionState);
            ArgumentOutOfRangeException.ThrowIfNegative(availableNestedSlots);
            ArgumentOutOfRangeException.ThrowIfNegative(availablePrepareSlots);

            if (availableNestedSlots == 0 && availablePrepareSlots == 0)
            {
                return [];
            }

            PlanningTree tree = BuildPlanningTree(facts, selectionState);
            HashSet<string> candidateStableIds = GetCandidateStableIds(tree);
            List<DiscoveryWorkItem> planned = [];

            foreach (PlanningNode node in GetDiscoveryFrontierNodes(tree))
            {
                switch (node.Fact.Tile.ContentKind)
                {
                    case TileContentKind.Json:
                        if (availableNestedSlots == 0)
                        {
                            break;
                        }

                        if (node.Fact.Tile.Depth < facts.Request.Traversal.MaxDepth &&
                            node.Fact.NestedStatus == ContentDiscoveryStatus.Unrequested)
                        {
                            planned.Add(new LoadNestedTilesetWorkItem(node.Fact.Tile));
                            availableNestedSlots--;
                        }

                        break;

                    case TileContentKind.Glb:
                        if (availablePrepareSlots == 0)
                        {
                            break;
                        }

                        if (!candidateStableIds.Contains(node.StableId))
                        {
                            break;
                        }

                        if (tree.PlanningVisibleStableIds.Count == 0 &&
                            HasNestedRelayDescendant(node) &&
                            !ShouldPrioritizeCoverage(facts.Request, node.Fact.Tile))
                        {
                            break;
                        }

                        if (tree.PlanningVisibleStableIds.Contains(node.StableId))
                        {
                            break;
                        }

                        if (tree.AncestorsWithPlanningVisibleDescendants.Contains(node.StableId) &&
                            !ShouldPrioritizeCoverage(facts.Request, node.Fact.Tile))
                        {
                            break;
                        }

                        if (node.Fact.PrepareStatus == ContentDiscoveryStatus.Unrequested)
                        {
                            planned.Add(new PrepareTileWorkItem(node.Fact.Tile));
                            availablePrepareSlots--;
                        }

                        break;
                }

                if (availableNestedSlots == 0 && availablePrepareSlots == 0)
                {
                    break;
                }
            }

            return planned;
        }

        internal void ApplyDiscoveryCompletion(
            DiscoveryFacts facts,
            DiscoveryCompletion completion,
            ref long nextPreparedOrder,
            ref int processedTiles,
            ref int failedTiles)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(completion);

            string stableId = completion.Tile.StableId!;
            if (!facts.Branches.TryGetValue(stableId, out TileBranchFact? fact))
            {
                return;
            }

            switch (completion)
            {
                case NestedTilesetDiscovered nested:
                    fact.NestedStatus = ContentDiscoveryStatus.Ready;
                    facts.TilesetCache[nested.Tile.ContentUri.AbsoluteUri] = nested.Tileset;
                    processedTiles++;
                    ExpandDiscoveredTree(
                        facts,
                        nested.Tileset,
                        nested.Tile.WorldTransform,
                        nested.Tile.TileId,
                        nested.Tile.Depth + 1,
                        nested.Tile.TileId,
                        nested.Tile.StableId);
                    break;

                case TilePrepared prepared:
                    fact.PrepareStatus = ContentDiscoveryStatus.Ready;
                    fact.PreparedContent = prepared.Content;
                    fact.AssetCopyright = prepared.Content.AssetCopyright;
                    fact.PreparedOrder = nextPreparedOrder++;
                    fact.SendFailed = false;
                    break;

                case DiscoverySkipped skipped:
                    if (fact.Tile.ContentKind == TileContentKind.Json)
                    {
                        fact.NestedStatus = ContentDiscoveryStatus.Skipped;
                    }
                    else
                    {
                        fact.PrepareStatus = ContentDiscoveryStatus.Skipped;
                    }

                    processedTiles++;
                    if (skipped.Error is not null)
                    {
                        failedTiles++;
                    }

                    break;

                case DiscoveryFailed failed:
                    if (fact.Tile.ContentKind == TileContentKind.Json)
                    {
                        fact.NestedStatus = ContentDiscoveryStatus.Failed;
                    }
                    else
                    {
                        fact.PrepareStatus = ContentDiscoveryStatus.Failed;
                    }

                    processedTiles++;
                    failedTiles++;
                    break;
            }
        }

        internal Dictionary<string, RetainedTileState> BuildVisibleTiles(WriterState writerState)
        {
            ArgumentNullException.ThrowIfNull(writerState);
            return writerState.VisibleTiles
                .Where(static pair => pair.Value.SlotIds.Count > 0)
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);
        }

        internal InteractiveRunCheckpoint BuildCheckpoint(DiscoveryFacts facts)
        {
            ArgumentNullException.ThrowIfNull(facts);
            return new InteractiveRunCheckpoint(new Dictionary<string, Tileset>(facts.TilesetCache, StringComparer.OrdinalIgnoreCase));
        }

        internal int CountCandidateTiles(DiscoveryFacts facts, SelectionState selectionState)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(selectionState);
            return GetCandidateStableIds(BuildPlanningTree(facts, selectionState)).Count;
        }

        internal PlanningTree BuildPlanningTreeForSelection(DiscoveryFacts facts, SelectionState selectionState)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(selectionState);
            return BuildPlanningTree(facts, selectionState);
        }

        private void ExpandDiscoveredTree(
            DiscoveryFacts facts,
            Tileset tileset,
            Matrix4x4d parentWorld,
            string idPrefix,
            int depthOffset,
            string? parentContentTileId,
            string? parentContentStableId)
        {
            IReadOnlyList<TileSelectionResult> selected = _selector.Select(
                tileset,
                facts.Request.SelectionReference,
                facts.Range,
                facts.Request.Traversal.MaxDepth,
                facts.Request.Traversal.DetailTargetM,
                maxTiles: 0,
                parentWorld,
                idPrefix,
                depthOffset,
                parentContentTileId,
                parentContentStableId);

            foreach (TileSelectionResult tile in selected)
            {
                RegisterSelectedTile(facts, tile);
            }

            foreach (TileSelectionResult tile in selected.Where(static tile => tile.ContentKind == TileContentKind.Json))
            {
                if (!facts.Branches.TryGetValue(tile.StableId!, out TileBranchFact? fact))
                {
                    continue;
                }

                if (facts.TilesetCache.TryGetValue(tile.ContentUri.AbsoluteUri, out Tileset? cached) && !fact.NestedExpanded)
                {
                    fact.NestedStatus = ContentDiscoveryStatus.Ready;
                    fact.NestedExpanded = true;
                    ExpandDiscoveredTree(
                        facts,
                        cached,
                        tile.WorldTransform,
                        tile.TileId,
                        tile.Depth + 1,
                        tile.TileId,
                        tile.StableId);
                }
            }
        }

        private static void RegisterSelectedTile(DiscoveryFacts facts, TileSelectionResult tile)
        {
            string stableId = tile.StableId!;
            if (!facts.Branches.TryGetValue(stableId, out TileBranchFact? fact))
            {
                fact = new TileBranchFact(tile);
                facts.Branches.Add(stableId, fact);
                return;
            }

            if (fact.Tile != tile)
            {
                facts.Branches[stableId] = new TileBranchFact(tile)
                {
                    PrepareStatus = fact.PrepareStatus,
                    NestedStatus = fact.NestedStatus,
                    PreparedContent = fact.PreparedContent,
                    PreparedOrder = fact.PreparedOrder,
                    NestedExpanded = fact.NestedExpanded,
                    SendFailed = fact.SendFailed,
                    AssetCopyright = fact.AssetCopyright,
                    CompleteSendFailureCount = fact.CompleteSendFailureCount
                };
            }
        }

        private static PlanningTree BuildPlanningTree(DiscoveryFacts facts, SelectionState selectionState)
        {
            List<SelectionVisibleTile> planningVisibleTiles = GetPlanningVisibleTiles(facts, selectionState);
            HashSet<string> selectedStableIds = facts.Branches.Keys.ToHashSet(StringComparer.Ordinal);
            HashSet<string> planningVisibleStableIds = planningVisibleTiles
                .Select(static tile => tile.StableId)
                .Where(selectedStableIds.Contains)
                .ToHashSet(StringComparer.Ordinal);

            var nodes = facts.Branches.ToDictionary(
                static pair => pair.Key,
                static pair => new PlanningNode(pair.Value),
                StringComparer.Ordinal);
            var roots = new List<PlanningNode>();

            foreach ((string stableId, PlanningNode node) in nodes)
            {
                string? parentStableId = node.Fact.Tile.ParentStableId;
                if (!string.IsNullOrWhiteSpace(parentStableId) &&
                    nodes.TryGetValue(parentStableId, out PlanningNode? parent))
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                    continue;
                }

                roots.Add(node);
            }

            SortNodes(roots);

            return new PlanningTree(
                facts.Request,
                nodes,
                roots,
                planningVisibleTiles,
                selectedStableIds,
                planningVisibleStableIds,
                BuildAncestorsWithVisibleDescendants(planningVisibleTiles),
                BuildAncestorsWithVisibleDescendants(
                    planningVisibleTiles.Where(tile => !selectedStableIds.Contains(tile.StableId))));
        }

        private static void SortNodes(List<PlanningNode> nodes)
        {
            nodes.Sort(static (left, right) =>
            {
                int byDepth = left.Fact.Tile.Depth.CompareTo(right.Fact.Tile.Depth);
                if (byDepth != 0)
                {
                    return byDepth;
                }

                int bySpan = -Comparer<double>.Default.Compare(
                    left.Fact.Tile.HorizontalSpanM ?? double.MinValue,
                    right.Fact.Tile.HorizontalSpanM ?? double.MinValue);
                if (bySpan != 0)
                {
                    return bySpan;
                }

                return StringComparer.Ordinal.Compare(left.Fact.Tile.TileId, right.Fact.Tile.TileId);
            });

            foreach (PlanningNode node in nodes)
            {
                if (node.Children.Count > 0)
                {
                    SortNodes(node.Children);
                }
            }
        }

        private static void PopulateBootstrapDesired(
            PlanningTree tree,
            SelectionState selectionState,
            HashSet<string> candidateStableIds,
            HashSet<string> desired)
        {
            foreach (PlanningNode node in GetRenderableFrontierNodes(tree)
                         .Where(node => candidateStableIds.Contains(node.StableId)))
            {
                if (HasBlockingDesiredAncestor(node.Parent, desired, tree.PlanningVisibleStableIds))
                {
                    continue;
                }

                if (HasDesiredDescendant(node, desired))
                {
                    continue;
                }

                if (!IsRenderableAvailable(node.Fact, selectionState))
                {
                    continue;
                }

                if (tree.AncestorsWithOutOfSelectionVisibleDescendants.Contains(node.StableId))
                {
                    continue;
                }

                _ = desired.Add(node.StableId);
            }
        }

        private static void PopulateReplacementDesired(
            PlanningTree tree,
            SelectionState selectionState,
            HashSet<string> candidateStableIds,
            HashSet<string> desired)
        {
            var branchHasCandidatesMemo = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (SelectionVisibleTile visibleTile in tree.PlanningVisibleTiles
                         .OrderBy(static tile => tile.AncestorStableIds.Count)
                         .ThenBy(tile => tree.Nodes.TryGetValue(tile.StableId, out PlanningNode? node)
                             ? node.Fact.Tile.DistanceToReferenceM ?? double.MaxValue
                             : double.MaxValue)
                         .ThenBy(static tile => tile.StableId, StringComparer.Ordinal))
            {
                if (!tree.Nodes.TryGetValue(visibleTile.StableId, out PlanningNode? visibleNode))
                {
                    continue;
                }

                if (tree.AncestorsWithOutOfSelectionVisibleDescendants.Contains(visibleNode.StableId))
                {
                    continue;
                }

                PlanningNode[] requiredReplacementFrontier = GetRequiredReplacementFrontier(
                    visibleNode,
                    candidateStableIds,
                    branchHasCandidatesMemo);

                foreach (PlanningNode replacement in requiredReplacementFrontier)
                {
                    if (!IsRenderableAvailable(replacement.Fact, selectionState))
                    {
                        continue;
                    }

                    if (HasBlockingDesiredAncestor(replacement.Parent, desired, tree.PlanningVisibleStableIds))
                    {
                        continue;
                    }

                    _ = desired.Add(replacement.StableId);
                }

                bool allReplacementVisible = requiredReplacementFrontier.Length > 0 &&
                    requiredReplacementFrontier.All(replacement => tree.PlanningVisibleStableIds.Contains(replacement.StableId));
                if (!allReplacementVisible && IsRenderableAvailable(visibleNode.Fact, selectionState))
                {
                    _ = desired.Add(visibleNode.StableId);
                }
            }

            foreach (PlanningNode anchor in GetFrontierAnchors(tree))
            {
                if (tree.PlanningVisibleStableIds.Contains(anchor.StableId) ||
                    tree.AncestorsWithPlanningVisibleDescendants.Contains(anchor.StableId) ||
                    HasVisibleAncestor(anchor.Parent, tree.PlanningVisibleStableIds))
                {
                    continue;
                }

                foreach (PlanningNode node in GetRenderableFrontierNodes(anchor)
                             .Where(node => candidateStableIds.Contains(node.StableId)))
                {
                    if (HasBlockingDesiredAncestor(node.Parent, desired, tree.PlanningVisibleStableIds))
                    {
                        continue;
                    }

                    if (HasDesiredDescendant(node, desired))
                    {
                        continue;
                    }

                    if (!IsRenderableAvailable(node.Fact, selectionState))
                    {
                        continue;
                    }

                    if (tree.AncestorsWithOutOfSelectionVisibleDescendants.Contains(node.StableId))
                    {
                        continue;
                    }

                    _ = desired.Add(node.StableId);
                }
            }
        }

        private static bool HasBlockingDesiredAncestor(
            PlanningNode? parent,
            HashSet<string> desiredStableIds,
            IReadOnlySet<string> visibleStableIds)
        {
            PlanningNode? current = parent;
            while (current is not null)
            {
                if (desiredStableIds.Contains(current.StableId) &&
                    !visibleStableIds.Contains(current.StableId))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private static bool HasDesiredDescendant(
            PlanningNode node,
            HashSet<string> desiredStableIds)
        {
            foreach (PlanningNode child in node.Children)
            {
                if (desiredStableIds.Contains(child.StableId) ||
                    HasDesiredDescendant(child, desiredStableIds))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool BranchHasCandidate(
            PlanningNode node,
            HashSet<string> candidateStableIds,
            Dictionary<string, bool> memo)
        {
            if (memo.TryGetValue(node.StableId, out bool cached))
            {
                return cached;
            }

            if (candidateStableIds.Contains(node.StableId))
            {
                memo[node.StableId] = true;
                return true;
            }

            foreach (PlanningNode child in node.Children)
            {
                if (BranchHasCandidate(child, candidateStableIds, memo))
                {
                    memo[node.StableId] = true;
                    return true;
                }
            }

            memo[node.StableId] = false;
            return false;
        }

        private static PlanningNode[] GetRequiredReplacementFrontier(
            PlanningNode node,
            HashSet<string> candidateStableIds,
            Dictionary<string, bool> branchHasCandidatesMemo)
        {
            var frontier = new List<PlanningNode>();
            foreach (PlanningNode child in node.Children)
            {
                AddRequiredReplacementFrontier(child, candidateStableIds, branchHasCandidatesMemo, frontier);
            }

            return [..frontier
                .DistinctBy(static replacement => replacement.StableId)
                .OrderBy(static replacement => replacement.Fact.Tile.Depth)
                .ThenBy(static replacement => replacement.Fact.Tile.DistanceToReferenceM ?? double.MaxValue)
                .ThenByDescending(static replacement => replacement.Fact.Tile.HorizontalSpanM ?? double.MinValue)
                .ThenBy(static replacement => replacement.Fact.Tile.TileId, StringComparer.Ordinal)];
        }

        private static void AddRequiredReplacementFrontier(
            PlanningNode node,
            HashSet<string> candidateStableIds,
            Dictionary<string, bool> branchHasCandidatesMemo,
            List<PlanningNode> frontier)
        {
            if (!BranchHasCandidate(node, candidateStableIds, branchHasCandidatesMemo))
            {
                return;
            }

            if (node.Fact.Tile.ContentKind == TileContentKind.Glb)
            {
                frontier.Add(node);
                return;
            }

            foreach (PlanningNode child in node.Children)
            {
                AddRequiredReplacementFrontier(child, candidateStableIds, branchHasCandidatesMemo, frontier);
            }
        }

        private static bool IsRenderableAvailable(TileBranchFact fact, SelectionState selectionState)
        {
            if (fact.SendFailed &&
                !selectionState.ContainsVisible(fact.Tile.StableId!))
            {
                return false;
            }

            return selectionState.ContainsVisible(fact.Tile.StableId!) ||
                   (fact.PrepareStatus == ContentDiscoveryStatus.Ready && fact.PreparedContent is not null);
        }

        private static List<SelectionVisibleTile> GetPlanningVisibleTiles(
            DiscoveryFacts facts,
            SelectionState selectionState)
        {
            if (!facts.RemoveOutOfRangeRetainedTiles)
            {
                return [..selectionState.VisibleTiles.Values];
            }

            return [..selectionState.VisibleTiles.Values
                .Where(tile => facts.Branches.ContainsKey(tile.StableId))
                ];
        }

        private static HashSet<string> GetCandidateStableIds(PlanningTree tree)
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal);

            foreach (PlanningNode node in GetRenderableFrontierNodes(tree))
            {
                if (candidates.Count >= tree.Request.Traversal.MaxTiles &&
                    !tree.PlanningVisibleStableIds.Contains(node.StableId))
                {
                    continue;
                }

                _ = candidates.Add(node.StableId);
            }

            foreach (string visibleStableId in tree.PlanningVisibleStableIds)
            {
                if (tree.Nodes.ContainsKey(visibleStableId))
                {
                    _ = candidates.Add(visibleStableId);
                }
            }

            return candidates;
        }

        private static PlanningNode[] GetRenderableFrontierNodes(PlanningTree tree)
        {
            var frontier = new List<PlanningNode>();
            foreach (PlanningNode anchor in GetFrontierAnchors(tree))
            {
                frontier.AddRange(GetRenderableFrontierNodes(anchor));
            }

            PlanningNode[] distinctFrontier = [..frontier
                .DistinctBy(static node => node.StableId)];
            bool hasCoverageCandidates = distinctFrontier.Any(node => ShouldPrioritizeCoverage(tree.Request, node.Fact.Tile));

            return distinctFrontier
                .OrderBy(node => hasCoverageCandidates && ShouldPrioritizeCoverage(tree.Request, node.Fact.Tile) ? 0 : 1)
                .ThenBy(node => hasCoverageCandidates ? node.Fact.Tile.Depth : -node.Fact.Tile.Depth)
                .ThenBy(static node => node.Fact.Tile.DistanceToReferenceM ?? double.MaxValue)
                .ThenByDescending(static node => node.Fact.Tile.HorizontalSpanM ?? double.MinValue)
                .ThenBy(static node => node.Fact.Tile.TileId, StringComparer.Ordinal)
                .ToArray();
        }

        private static PlanningNode[] GetRenderableFrontierNodes(PlanningNode anchor)
        {
            var frontier = new List<PlanningNode>();
            if (anchor.Fact.Tile.ContentKind == TileContentKind.Glb)
            {
                frontier.Add(anchor);
            }

            foreach (PlanningNode child in anchor.Children)
            {
                PlanningNode? candidate = FindFirstRenderableNode(child);
                if (candidate is not null)
                {
                    frontier.Add(candidate);
                }
            }

            return [..frontier
                .DistinctBy(static node => node.StableId)
                .OrderBy(static node => node.Fact.Tile.Depth)
                .ThenBy(static node => node.Fact.Tile.DistanceToReferenceM ?? double.MaxValue)
                .ThenByDescending(static node => node.Fact.Tile.HorizontalSpanM ?? double.MinValue)
                .ThenBy(static node => node.Fact.Tile.TileId, StringComparer.Ordinal)];
        }

        private static PlanningNode[] GetDiscoveryFrontierNodes(PlanningTree tree)
        {
            var frontier = new List<PlanningNode>();
            foreach (PlanningNode anchor in GetFrontierAnchors(tree))
            {
                frontier.Add(anchor);
                AddDiscoveryFrontierDescendants(anchor, frontier);
            }

            PlanningNode[] distinctFrontier = [..frontier
                .DistinctBy(static node => node.StableId)];
            bool hasCoverageCandidates = distinctFrontier.Any(node => ShouldPrioritizeCoverage(tree.Request, node.Fact.Tile));

            return distinctFrontier
                .OrderBy(node => tree.PlanningVisibleStableIds.Contains(node.StableId) ? 0 : 1)
                .ThenBy(node => hasCoverageCandidates && ShouldPrioritizeCoverage(tree.Request, node.Fact.Tile) ? 0 : 1)
                .ThenBy(node => hasCoverageCandidates ? node.Fact.Tile.Depth : -node.Fact.Tile.Depth)
                .ThenBy(static node => node.Fact.Tile.DistanceToReferenceM ?? double.MaxValue)
                .ThenByDescending(static node => node.Fact.Tile.HorizontalSpanM ?? double.MinValue)
                .ThenBy(static node => node.Fact.Tile.TileId, StringComparer.Ordinal)
                .ToArray();
        }

        private static void AddDiscoveryFrontierDescendants(PlanningNode node, List<PlanningNode> frontier)
        {
            foreach (PlanningNode child in node.Children)
            {
                frontier.Add(child);
                if (child.Fact.Tile.ContentKind == TileContentKind.Json &&
                    child.Fact.NestedStatus == ContentDiscoveryStatus.Ready)
                {
                    AddDiscoveryFrontierDescendants(child, frontier);
                }
            }
        }

        private static PlanningNode[] GetFrontierAnchors(PlanningTree tree)
        {
            IEnumerable<PlanningNode> anchors;
            if (tree.PlanningVisibleStableIds.Count == 0)
            {
                anchors = tree.Roots;
            }
            else
            {
                var collected = new List<PlanningNode>();
                collected.AddRange(tree.Roots);
                foreach (string stableId in tree.PlanningVisibleStableIds.Where(tree.Nodes.ContainsKey))
                {
                    PlanningNode current = tree.Nodes[stableId];
                    while (true)
                    {
                        collected.Add(current);
                        if (current.Parent is null)
                        {
                            break;
                        }

                        current = current.Parent;
                    }
                }

                anchors = collected;
            }

            return anchors
                .DistinctBy(static node => node.StableId)
                .OrderBy(node => ShouldPrioritizeCoverage(tree.Request, node.Fact.Tile) ? 0 : 1)
                .ThenBy(static node => node.Fact.Tile.Depth)
                .ThenBy(static node => node.Fact.Tile.DistanceToReferenceM ?? double.MaxValue)
                .ThenByDescending(static node => node.Fact.Tile.HorizontalSpanM ?? double.MinValue)
                .ThenBy(static node => node.Fact.Tile.TileId, StringComparer.Ordinal)
                .ToArray();
        }

        private static PlanningNode? FindFirstRenderableNode(PlanningNode node)
        {
            if (node.Fact.Tile.ContentKind == TileContentKind.Glb)
            {
                return node;
            }

            foreach (PlanningNode child in node.Children)
            {
                PlanningNode? candidate = FindFirstRenderableNode(child);
                if (candidate is not null)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static HashSet<string> BuildAncestorsWithVisibleDescendants(IEnumerable<SelectionVisibleTile> visibleTiles)
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            foreach (SelectionVisibleTile tile in visibleTiles)
            {
                foreach (string ancestor in tile.AncestorStableIds)
                {
                    _ = ancestors.Add(ancestor);
                }
            }

            return ancestors;
        }

        private static bool HasVisibleAncestor(PlanningNode? parent, IReadOnlySet<string> visibleStableIds)
        {
            PlanningNode? current = parent;
            while (current is not null)
            {
                if (visibleStableIds.Contains(current.StableId))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private static bool HasNestedRelayDescendant(PlanningNode node)
        {
            foreach (PlanningNode child in node.Children)
            {
                if (child.Fact.Tile.ContentKind == TileContentKind.Json ||
                    HasNestedRelayDescendant(child))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasDesiredAncestor(PlanningNode? parent, HashSet<string> desiredStableIds)
        {
            PlanningNode? current = parent;
            while (current is not null)
            {
                if (desiredStableIds.Contains(current.StableId))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private static bool ShouldPrioritizeCoverage(TileRunRequest request, TileSelectionResult tile)
        {
            return tile.HasChildren &&
                   tile.HorizontalSpanM is double span &&
                   span >= request.Traversal.RangeM;
        }

    }
}
