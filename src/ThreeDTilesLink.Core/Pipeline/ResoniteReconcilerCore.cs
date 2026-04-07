using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class ResoniteReconcilerCore(TraversalCore traversalCore)
    {
        private static readonly TimeSpan VisibleReplacementGrace = TimeSpan.FromSeconds(1);
        private readonly TraversalCore _traversalCore = traversalCore;

        internal WriterPlan ReduceWriterPlan(
            DiscoveryFacts facts,
            WriterState writerState,
            SelectionState selectionState,
            DesiredView desiredView,
            ProgressSnapshot progress,
            bool dryRun,
            int maxConcurrentWriterSends)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            ArgumentNullException.ThrowIfNull(selectionState);
            ArgumentNullException.ThrowIfNull(desiredView);
            ArgumentNullException.ThrowIfNull(progress);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentWriterSends);

            WriterState planningState = writerState.CreatePlanningCopy();
            bool hasPendingRemovals = HasPendingRemovals(facts, planningState, desiredView);
            int sendConcurrencyLimit = hasPendingRemovals
                ? System.Math.Max(1, maxConcurrentWriterSends - 1)
                : maxConcurrentWriterSends;

            WriterCommand? controlCommand = null;
            if (planningState.InFlightRemoveStableId is null && !planningState.MetadataInFlight)
            {
                WriterCommand? nextControlCommand = PlanWriterCommand(
                    facts,
                    planningState,
                    desiredView,
                    progress,
                    dryRun,
                    allowRemoval: true,
                    allowSend: false,
                    allowMetadata: planningState.InFlightSendStableIds.Count == 0);
                if (nextControlCommand is RemoveTileWriterCommand or SyncSessionMetadataWriterCommand ||
                    (nextControlCommand is DelayWriterCommand && planningState.InFlightSendStableIds.Count == 0))
                {
                    controlCommand = nextControlCommand;
                    ApplyPlanningInFlight(planningState, nextControlCommand);
                }
            }

            List<SendTileWriterCommand> sendCommands = [];
            while (planningState.InFlightSendStableIds.Count < sendConcurrencyLimit)
            {
                WriterCommand? sendCommand = PlanWriterCommand(
                    facts,
                    planningState,
                    desiredView,
                    progress,
                    dryRun,
                    allowRemoval: false,
                    allowSend: true,
                    allowMetadata: false);
                if (sendCommand is not SendTileWriterCommand sendTileCommand)
                {
                    break;
                }

                sendCommands.Add(sendTileCommand);
                ApplyPlanningInFlight(planningState, sendTileCommand);
            }

            return new WriterPlan(controlCommand, sendCommands, hasPendingRemovals);
        }

        internal WriterCommand? PlanNextWriterCommand(
            DiscoveryFacts facts,
            WriterState writerState,
            DesiredView desiredView,
            ProgressSnapshot progress,
            bool dryRun,
            bool allowRemoval = true,
            bool allowSend = true,
            bool allowMetadata = true,
            DateTimeOffset? now = null)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            ArgumentNullException.ThrowIfNull(desiredView);
            ArgumentNullException.ThrowIfNull(progress);

            return PlanWriterCommand(
                facts,
                writerState,
                desiredView,
                progress,
                dryRun,
                allowRemoval,
                allowSend,
                allowMetadata,
                now);
        }

        internal static void ApplyWriterCompletion(
            DiscoveryFacts facts,
            WriterState writerState,
            WriterCompletion completion,
            bool dryRun,
            ref int processedTiles,
            ref int streamedMeshes,
            ref int failedTiles,
            DateTimeOffset? now = null)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            ArgumentNullException.ThrowIfNull(completion);

            DateTimeOffset currentTime = now ?? DateTimeOffset.UtcNow;

            switch (completion)
            {
                case SendTileCompleted sent:
                {
                    string stableId = sent.Content.Tile.StableId!;
                    _ = writerState.InFlightSendStableIds.Remove(stableId);
                    processedTiles++;
                    streamedMeshes += sent.StreamedMeshCount;

                    if (!facts.Branches.TryGetValue(stableId, out TileBranchFact? fact))
                    {
                        if (!sent.Succeeded)
                        {
                            failedTiles++;
                        }

                        break;
                    }

                    if (!sent.Succeeded)
                    {
                        failedTiles++;
                        fact.SendFailed = sent.SlotIds.Count == 0 &&
                            !string.IsNullOrWhiteSpace(fact.Tile.ParentStableId);
                        if (sent.SlotIds.Count == 0)
                        {
                            fact.CompleteSendFailureCount++;
                            fact.PrepareStatus = fact.CanRetryCompleteSendFailure
                                ? ContentDiscoveryStatus.Ready
                                : ContentDiscoveryStatus.Failed;
                        }
                    }
                    else
                    {
                        fact.SendFailed = false;
                    }

                    if (sent.Succeeded || sent.SlotIds.Count > 0 || dryRun)
                    {
                        writerState.VisibleTiles[stableId] = new RetainedTileState(
                            stableId,
                            sent.Content.Tile.TileId,
                            sent.Content.Tile.ParentStableId,
                            GetAncestorStableIds(facts, sent.Content.Tile.ParentStableId),
                            sent.SlotIds,
                            sent.Content.AssetCopyright);
                        writerState.VisibleSinceByStableId[stableId] = currentTime;
                    }

                    fact.AssetCopyright = sent.Content.AssetCopyright;
                    if (sent.Succeeded || sent.SlotIds.Count > 0 || dryRun)
                    {
                        fact.CompleteSendFailureCount = 0;
                        fact.PreparedContent = null;
                    }

                    break;
                }

                case RemoveTileCompleted removed:
                    writerState.InFlightRemoveStableId = null;
                    if (removed.Succeeded)
                    {
                        _ = writerState.FailedRemovalStableIds.Remove(removed.StableId);
                        _ = writerState.VisibleTiles.Remove(removed.StableId);
                        _ = writerState.VisibleSinceByStableId.Remove(removed.StableId);
                    }
                    else
                    {
                        failedTiles += System.Math.Max(1, removed.FailedSlotCount);
                        _ = writerState.FailedRemovalStableIds.Add(removed.StableId);
                        if (removed.RemainingSlotIds.Count > 0 &&
                            writerState.VisibleTiles.TryGetValue(removed.StableId, out RetainedTileState? visibleTile))
                        {
                            writerState.VisibleTiles[removed.StableId] = visibleTile with
                            {
                                SlotIds = removed.RemainingSlotIds
                            };
                        }
                    }

                    break;

                case SyncSessionMetadataCompleted metadata:
                    writerState.MetadataInFlight = false;
                    writerState.AppliedLicenseCredit = metadata.LicenseCredit;
                    writerState.AppliedProgressValue = metadata.ProgressValue;
                    writerState.AppliedProgressText = metadata.ProgressText;
                    break;

                case DelayCompleted:
                    break;
            }
        }

        internal bool IsReconciled(
            DiscoveryFacts facts,
            WriterState writerState,
            DesiredView desiredView,
            ProgressSnapshot progress,
            bool dryRun)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            ArgumentNullException.ThrowIfNull(desiredView);
            ArgumentNullException.ThrowIfNull(progress);

            HashSet<string> actualVisible = writerState.VisibleTiles.Keys
                .Where(desiredView.SelectedStableIds.Contains)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> expectedVisible = desiredView.StableIds
                .Concat(writerState.FailedRemovalStableIds.Where(writerState.VisibleTiles.ContainsKey))
                .ToHashSet(StringComparer.Ordinal);
            if (!actualVisible.SetEquals(expectedVisible))
            {
                return false;
            }

            if (dryRun)
            {
                return true;
            }

            (string desiredLicense, float desiredProgressValue, string desiredProgressText) = BuildDesiredMetadata(
                facts,
                writerState,
                desiredView,
                progress);

            return string.Equals(desiredLicense, writerState.AppliedLicenseCredit, StringComparison.Ordinal) &&
                   string.Equals(desiredProgressText, writerState.AppliedProgressText, StringComparison.Ordinal) &&
                   System.Math.Abs(desiredProgressValue - writerState.AppliedProgressValue) <= 0.0001f;
        }

        private WriterCommand? PlanWriterCommand(
            DiscoveryFacts facts,
            WriterState writerState,
            DesiredView desiredView,
            ProgressSnapshot progress,
            bool dryRun,
            bool allowRemoval,
            bool allowSend,
            bool allowMetadata,
            DateTimeOffset? now = null)
        {
            PlanningTree tree = _traversalCore.BuildPlanningTreeForSelection(facts, writerState.CreateSelectionState());
            DateTimeOffset currentTime = now ?? DateTimeOffset.UtcNow;
            HashSet<string> selectionStableIds = desiredView.SelectedStableIds is HashSet<string> hashSet
                ? hashSet
                : desiredView.SelectedStableIds.ToHashSet(StringComparer.Ordinal);
            var branchHasSelectionMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
            var replacementReadyAtMemo = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);

            if (writerState.InFlightRemoveStableId is not null || writerState.MetadataInFlight)
            {
                return null;
            }

            DateTimeOffset? deferredRemovalDueAt = null;
            if (allowRemoval)
            {
                RetainedTileState? removal = writerState.VisibleTiles.Values
                    .Where(tile => desiredView.SelectedStableIds.Contains(tile.StableId))
                    .Where(tile => !desiredView.StableIds.Contains(tile.StableId))
                    .Where(tile => !writerState.FailedRemovalStableIds.Contains(tile.StableId))
                    .Where(tile => !HasInFlightRelatedSend(
                        tree.Nodes.TryGetValue(tile.StableId, out PlanningNode? node) ? node : null,
                        writerState.InFlightSendStableIds))
                    .Select(tile => new
                    {
                        Tile = tile,
                        ReadyAt = GetReplacementReadyAt(
                            tile.StableId,
                            tree,
                            writerState,
                            selectionStableIds,
                            branchHasSelectionMemo,
                            replacementReadyAtMemo)
                    })
                    .Where(entry => entry.ReadyAt is not null)
                    .Select(entry =>
                    {
                        if (entry.ReadyAt > currentTime &&
                            (deferredRemovalDueAt is null || entry.ReadyAt < deferredRemovalDueAt))
                        {
                            deferredRemovalDueAt = entry.ReadyAt;
                        }

                        return entry;
                    })
                    .Where(entry => entry.ReadyAt <= currentTime)
                    .Select(entry => entry.Tile)
                    .OrderBy(tile => facts.Branches.TryGetValue(tile.StableId, out TileBranchFact? fact) ? fact.Tile.Depth : int.MaxValue)
                    .ThenBy(tile => tile.TileId, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (removal is not null)
                {
                    return new RemoveTileWriterCommand(removal.StableId, removal.TileId, removal.SlotIds);
                }
            }

            if (allowSend)
            {
                TileBranchFact? coverageFact = tree.Nodes.Values
                    .Select(static node => node.Fact)
                    .Where(static fact => fact.Tile.ContentKind == TileContentKind.Glb)
                    .Where(fact => desiredView.CandidateStableIds.Contains(fact.Tile.StableId!))
                    .Where(fact => !tree.PlanningVisibleStableIds.Contains(fact.Tile.StableId!))
                    .Where(fact => !writerState.InFlightSendStableIds.Contains(fact.Tile.StableId!))
                    .Where(fact => !HasInFlightRelatedSend(
                        tree.Nodes.TryGetValue(fact.Tile.StableId!, out PlanningNode? node) ? node : null,
                        writerState.InFlightSendStableIds))
                    .Where(fact => fact.PreparedContent is not null && fact.PrepareStatus == ContentDiscoveryStatus.Ready)
                    .Where(fact => !HasVisibleAncestor(
                        tree.Nodes.TryGetValue(fact.Tile.StableId!, out PlanningNode? node) ? node.Parent : null,
                        tree.PlanningVisibleStableIds))
                    .Where(fact => fact.Tile.HasChildren && fact.Tile.HorizontalSpanM is double span &&
                        span > facts.Request.Traversal.RangeM *
                        (facts.Request.Traversal.BootstrapRangeMultiplier > 0d
                            ? facts.Request.Traversal.BootstrapRangeMultiplier
                            : 4d))
                    .OrderBy(static fact => fact.Tile.Depth)
                    .ThenByDescending(static fact => fact.Tile.HorizontalSpanM ?? double.MinValue)
                    .ThenBy(static fact => fact.PreparedOrder)
                    .FirstOrDefault();

                if (coverageFact?.PreparedContent is not null)
                {
                    return new SendTileWriterCommand(coverageFact.PreparedContent);
                }

                TileBranchFact? sendFact = desiredView.StableIds
                    .Where(stableId => !tree.PlanningVisibleStableIds.Contains(stableId))
                    .Where(stableId => !writerState.InFlightSendStableIds.Contains(stableId))
                    .Where(stableId => !HasInFlightRelatedSend(
                        tree.Nodes.TryGetValue(stableId, out PlanningNode? node) ? node : null,
                        writerState.InFlightSendStableIds))
                    .Where(stableId => facts.Branches.TryGetValue(stableId, out TileBranchFact? fact) &&
                                       fact.PreparedContent is not null &&
                                       fact.PrepareStatus == ContentDiscoveryStatus.Ready)
                    .Select(stableId => facts.Branches[stableId])
                    .OrderBy(fact => GetNearestVisibleAncestorDepth(
                        tree.Nodes.TryGetValue(fact.Tile.StableId!, out PlanningNode? node) ? node.Parent : null,
                        tree.PlanningVisibleStableIds))
                    .ThenBy(static fact => fact.Tile.Depth)
                    .ThenByDescending(static fact => fact.Tile.HorizontalSpanM ?? double.MinValue)
                    .ThenBy(static fact => fact.PreparedOrder)
                    .FirstOrDefault();

                if (sendFact?.PreparedContent is not null)
                {
                    return new SendTileWriterCommand(sendFact.PreparedContent);
                }
            }

            if (!dryRun && allowMetadata && writerState.InFlightSendStableIds.Count == 0)
            {
                (string desiredLicense, float desiredProgressValue, string desiredProgressText) = BuildDesiredMetadata(
                    facts,
                    writerState,
                    desiredView,
                    progress);
                bool metadataChanged =
                    !string.Equals(desiredLicense, writerState.AppliedLicenseCredit, StringComparison.Ordinal) ||
                    System.Math.Abs(desiredProgressValue - writerState.AppliedProgressValue) > 0.0001f ||
                    !string.Equals(desiredProgressText, writerState.AppliedProgressText, StringComparison.Ordinal);
                if (metadataChanged)
                {
                    return new SyncSessionMetadataWriterCommand(desiredLicense, desiredProgressValue, desiredProgressText);
                }
            }

            if (deferredRemovalDueAt is DateTimeOffset dueAt && dueAt > currentTime)
            {
                return new DelayWriterCommand(dueAt - currentTime);
            }

            return null;
        }

        private bool HasPendingRemovals(DiscoveryFacts facts, WriterState writerState, DesiredView desiredView)
            => CountPendingRemovals(facts, writerState, desiredView) > 0;

        private int CountPendingRemovals(DiscoveryFacts facts, WriterState writerState, DesiredView desiredView)
        {
            PlanningTree tree = _traversalCore.BuildPlanningTreeForSelection(facts, writerState.CreateSelectionState());
            HashSet<string> selectionStableIds = desiredView.SelectedStableIds is HashSet<string> selectedHashSet
                ? selectedHashSet
                : desiredView.SelectedStableIds.ToHashSet(StringComparer.Ordinal);
            var branchHasSelectionMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
            var replacementReadyAtMemo = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);

            return writerState.VisibleTiles.Values.Count(tile =>
                desiredView.SelectedStableIds.Contains(tile.StableId) &&
                !desiredView.StableIds.Contains(tile.StableId) &&
                !writerState.FailedRemovalStableIds.Contains(tile.StableId) &&
                GetReplacementReadyAt(
                    tile.StableId,
                    tree,
                    writerState,
                    selectionStableIds,
                    branchHasSelectionMemo,
                    replacementReadyAtMemo) is not null);
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

        private static int GetNearestVisibleAncestorDepth(PlanningNode? parent, IReadOnlySet<string> visibleStableIds)
        {
            PlanningNode? current = parent;
            while (current is not null)
            {
                if (visibleStableIds.Contains(current.StableId))
                {
                    return current.Fact.Tile.Depth;
                }

                current = current.Parent;
            }

            return int.MaxValue;
        }

        private static bool HasInFlightRelatedSend(PlanningNode? node, HashSet<string> inFlightSendStableIds)
        {
            if (node is null || inFlightSendStableIds.Count == 0)
            {
                return false;
            }

            PlanningNode? current = node.Parent;
            while (current is not null)
            {
                if (inFlightSendStableIds.Contains(current.StableId))
                {
                    return true;
                }

                current = current.Parent;
            }

            return HasInFlightDescendant(node, inFlightSendStableIds);
        }

        private static bool HasInFlightDescendant(PlanningNode node, HashSet<string> inFlightSendStableIds)
        {
            foreach (PlanningNode child in node.Children)
            {
                if (inFlightSendStableIds.Contains(child.StableId) ||
                    HasInFlightDescendant(child, inFlightSendStableIds))
                {
                    return true;
                }
            }

            return false;
        }

        private static DateTimeOffset? GetReplacementReadyAt(
            string stableId,
            PlanningTree tree,
            WriterState writerState,
            HashSet<string> selectionStableIds,
            Dictionary<string, bool> branchHasSelectionMemo,
            Dictionary<string, DateTimeOffset?> memo)
        {
            if (!tree.Nodes.TryGetValue(stableId, out PlanningNode? node) || node.Children.Count == 0)
            {
                return null;
            }

            DateTimeOffset? latest = null;
            bool hasRelevantChild = false;

            foreach (PlanningNode child in node.Children)
            {
                if (!BranchHasSelectedTile(child, selectionStableIds, branchHasSelectionMemo))
                {
                    continue;
                }

                hasRelevantChild = true;
                DateTimeOffset? childReadyAt = GetVisibleCoverageReadyAt(
                    child,
                    tree,
                    writerState,
                    selectionStableIds,
                    branchHasSelectionMemo,
                    memo);
                if (childReadyAt is null)
                {
                    return null;
                }

                if (latest is null || childReadyAt > latest)
                {
                    latest = childReadyAt;
                }
            }

            return hasRelevantChild ? latest : null;
        }

        private static DateTimeOffset? GetVisibleCoverageReadyAt(
            PlanningNode node,
            PlanningTree tree,
            WriterState writerState,
            HashSet<string> selectionStableIds,
            Dictionary<string, bool> branchHasSelectionMemo,
            Dictionary<string, DateTimeOffset?> memo)
        {
            if (memo.TryGetValue(node.StableId, out DateTimeOffset? cached))
            {
                return cached;
            }

            if (tree.PlanningVisibleStableIds.Contains(node.StableId))
            {
                DateTimeOffset readyAt = GetVisibleReadyAt(node.StableId, writerState);
                memo[node.StableId] = readyAt;
                return readyAt;
            }

            if (node.Children.Count == 0)
            {
                memo[node.StableId] = null;
                return null;
            }

            DateTimeOffset? latest = null;
            bool hasRelevantChild = false;
            foreach (PlanningNode child in node.Children)
            {
                if (!BranchHasSelectedTile(child, selectionStableIds, branchHasSelectionMemo))
                {
                    continue;
                }

                hasRelevantChild = true;
                DateTimeOffset? childReadyAt = GetVisibleCoverageReadyAt(
                    child,
                    tree,
                    writerState,
                    selectionStableIds,
                    branchHasSelectionMemo,
                    memo);
                if (childReadyAt is null)
                {
                    memo[node.StableId] = null;
                    return null;
                }

                if (latest is null || childReadyAt > latest)
                {
                    latest = childReadyAt;
                }
            }

            memo[node.StableId] = hasRelevantChild ? latest : null;
            return memo[node.StableId];
        }

        private static DateTimeOffset GetVisibleReadyAt(string stableId, WriterState writerState)
        {
            DateTimeOffset visibleSince = writerState.VisibleSinceByStableId.TryGetValue(stableId, out DateTimeOffset timestamp)
                ? timestamp
                : DateTimeOffset.MinValue;
            return visibleSince == DateTimeOffset.MinValue
                ? DateTimeOffset.MinValue
                : visibleSince + VisibleReplacementGrace;
        }

        private static bool BranchHasSelectedTile(
            PlanningNode node,
            HashSet<string> selectionStableIds,
            Dictionary<string, bool> memo)
        {
            if (memo.TryGetValue(node.StableId, out bool cached))
            {
                return cached;
            }

            if (selectionStableIds.Contains(node.StableId))
            {
                memo[node.StableId] = true;
                return true;
            }

            foreach (PlanningNode child in node.Children)
            {
                if (BranchHasSelectedTile(child, selectionStableIds, memo))
                {
                    memo[node.StableId] = true;
                    return true;
                }
            }

            memo[node.StableId] = false;
            return false;
        }

        private static List<string> GetAncestorStableIds(DiscoveryFacts facts, string? parentStableId)
        {
            var ancestors = new List<string>();
            string? currentId = parentStableId;
            while (!string.IsNullOrWhiteSpace(currentId) &&
                   facts.Branches.TryGetValue(currentId, out TileBranchFact? parent))
            {
                ancestors.Add(parent.Tile.StableId!);
                currentId = parent.Tile.ParentStableId;
            }

            return ancestors;
        }

        private (string LicenseCredit, float ProgressValue, string ProgressText) BuildDesiredMetadata(
            DiscoveryFacts facts,
            WriterState writerState,
            DesiredView desiredView,
            ProgressSnapshot progress)
        {
            string desiredLicense = BuildDesiredLicense(writerState.VisibleTiles.Values);

            int pendingDiscovery = facts.Branches.Values.Count(fact =>
                (fact.Tile.ContentKind == TileContentKind.Json && fact.NestedStatus is ContentDiscoveryStatus.Unrequested or ContentDiscoveryStatus.InFlight) ||
                (fact.Tile.ContentKind == TileContentKind.Glb && desiredView.CandidateStableIds.Contains(fact.Tile.StableId!) &&
                 fact.PrepareStatus is ContentDiscoveryStatus.Unrequested or ContentDiscoveryStatus.InFlight));
            int pendingPrepared = facts.Branches.Values.Count(fact =>
                fact.Tile.ContentKind == TileContentKind.Glb &&
                desiredView.CandidateStableIds.Contains(fact.Tile.StableId!) &&
                fact.PreparedContent is not null &&
                fact.PrepareStatus == ContentDiscoveryStatus.Ready &&
                !writerState.VisibleTiles.ContainsKey(fact.Tile.StableId!));
            int pendingSend = desiredView.StableIds.Count(stableId => !writerState.VisibleTiles.ContainsKey(stableId));
            int pendingRemove = CountPendingRemovals(facts, writerState, desiredView);

            int completedUnits = progress.ProcessedTiles;
            int pendingUnits = pendingDiscovery + pendingPrepared + pendingSend + pendingRemove +
                writerState.InFlightSendStableIds.Count +
                (writerState.InFlightRemoveStableId is null ? 0 : 1) +
                (writerState.MetadataInFlight ? 1 : 0);
            int candidateBacklog = System.Math.Max(0, progress.CandidateTiles - completedUnits);
            int totalUnits = completedUnits + pendingUnits + candidateBacklog;
            float progressValue = pendingUnits == 0 || totalUnits == 0
                ? 1f
                : System.Math.Clamp((float)completedUnits / totalUnits, 0f, 1f);
            string progressText = pendingUnits == 0
                ? $"Completed: candidate={progress.CandidateTiles} processed={progress.ProcessedTiles} streamed={progress.StreamedMeshes} failed={progress.FailedTiles}"
                : $"Running: candidate={progress.CandidateTiles} processed={progress.ProcessedTiles} streamed={progress.StreamedMeshes} failed={progress.FailedTiles} queued-send={pendingSend}";

            return (desiredLicense, progressValue, progressText);
        }

        private static void ApplyPlanningInFlight(WriterState writerState, WriterCommand command)
        {
            switch (command)
            {
                case SendTileWriterCommand send:
                    _ = writerState.InFlightSendStableIds.Add(send.Content.Tile.StableId!);
                    break;
                case RemoveTileWriterCommand remove:
                    writerState.InFlightRemoveStableId = remove.StableId;
                    break;
                case SyncSessionMetadataWriterCommand:
                    writerState.MetadataInFlight = true;
                    break;
            }
        }

        private static string BuildDesiredLicense(IEnumerable<RetainedTileState> visibleTiles)
        {
            var aggregator = new LicenseCreditAggregator();
            foreach (RetainedTileState retainedTile in visibleTiles)
            {
                IReadOnlyList<string> owners = LicenseCreditAggregator.ParseOwners(
                    string.IsNullOrWhiteSpace(retainedTile.AssetCopyright) ? [] : [retainedTile.AssetCopyright]);
                aggregator.RegisterOrder(owners);
                _ = aggregator.Activate(owners);
            }

            string built = aggregator.BuildCreditString();
            return string.IsNullOrWhiteSpace(built) ? "Google Maps" : built;
        }
    }
}
