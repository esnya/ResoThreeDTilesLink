using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

#pragma warning disable CA1822

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class SceneReconcilerCore(ILicenseCreditPolicy licenseCreditPolicy)
    {
        private readonly ILicenseCreditPolicy _licenseCreditPolicy = licenseCreditPolicy;
        private static readonly TimeSpan MetadataCadence = TimeSpan.FromMilliseconds(250);
        private const int MetadataProcessedDeltaThreshold = 8;
        private const float MetadataProgressDeltaThreshold = 0.02f;

        internal WriterPlan ReduceWriterPlan(
            DiscoveryFacts facts,
            WriterState writerState,
            SelectionState selectionState,
            DesiredView desiredView,
            ProgressSnapshot progress,
            bool dryRun,
            int maxConcurrentWriterSends,
            DateTimeOffset? now = null)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            ArgumentNullException.ThrowIfNull(selectionState);
            ArgumentNullException.ThrowIfNull(desiredView);
            ArgumentNullException.ThrowIfNull(progress);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentWriterSends);

            DateTimeOffset planningTime = now ?? DateTimeOffset.UtcNow;
            WriterReductionState reductionState = BuildWriterReductionState(
                writerState,
                desiredView,
                maxConcurrentWriterSends);

            WriterCommand? controlCommand = null;
            if (reductionState.PlanningState.InFlightRemoveStableId is null)
            {
                WriterCommand? nextControlCommand = PlanWriterCommand(
                    facts,
                    reductionState.PlanningState,
                    desiredView,
                    progress,
                    dryRun,
                    allowRemoval: true,
                    allowSend: false,
                    allowMetadata: true,
                    planningTime);
                if (nextControlCommand is RemoveTileWriterCommand or CleanupTileWriterCommand or SyncSessionMetadataWriterCommand)
                {
                    controlCommand = nextControlCommand;
                    ApplyPlanningInFlight(reductionState.PlanningState, nextControlCommand, planningTime);
                }
            }

            List<SendTileWriterCommand> sendCommands = [];
            while (reductionState.PlanningState.InFlightSendStableIds.Count < reductionState.SendConcurrencyLimit)
            {
                WriterCommand? sendCommand = PlanWriterCommand(
                    facts,
                    reductionState.PlanningState,
                    desiredView,
                    progress,
                    dryRun,
                    allowRemoval: false,
                    allowSend: true,
                    allowMetadata: false,
                    planningTime);
                if (sendCommand is not SendTileWriterCommand sendTileCommand)
                {
                    break;
                }

                sendCommands.Add(sendTileCommand);
                ApplyPlanningInFlight(reductionState.PlanningState, sendTileCommand, planningTime);
            }

            return new WriterPlan(controlCommand, sendCommands, reductionState.HasPendingRemovals);
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
                now ?? DateTimeOffset.UtcNow);
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
                    bool hasRetainedPartialNodes = !sent.Succeeded && sent.NodeIds.Count > 0;
                    bool shouldRetainVisibleNodes = sent.Succeeded || dryRun;
                    bool canRetrySendFailure = false;

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
                        fact.CompleteSendFailureCount++;
                        canRetrySendFailure = fact.CanRetryCompleteSendFailure;
                        fact.SendFailed = !string.IsNullOrWhiteSpace(fact.Tile.ParentStableId) &&
                            (!hasRetainedPartialNodes || !canRetrySendFailure);
                        if (!canRetrySendFailure)
                        {
                            fact.PrepareStatus = ContentDiscoveryStatus.Failed;
                            fact.PreparedContent = null;
                        }
                        else if (!hasRetainedPartialNodes)
                        {
                            fact.PrepareStatus = ContentDiscoveryStatus.Ready;
                        }
                    }
                    else
                    {
                        fact.SendFailed = false;
                    }

                    if (shouldRetainVisibleNodes)
                    {
                        writerState.VisibleTiles[stableId] = new RetainedTileState(
                            stableId,
                            sent.Content.Tile.TileId,
                            sent.Content.Tile.ParentStableId,
                            GetAncestorStableIds(facts, sent.Content.Tile.ParentStableId),
                            sent.NodeIds,
                            sent.Content.AssetCopyright);
                        if (!writerState.VisibleSinceByStableId.ContainsKey(stableId))
                        {
                            writerState.VisibleSinceByStableId[stableId] = currentTime;
                        }
                    }

                    if (hasRetainedPartialNodes)
                    {
                        writerState.CleanupDebtTiles[stableId] = MergeRetainedTileState(
                            writerState.CleanupDebtTiles,
                            stableId,
                            new RetainedTileState(
                                stableId,
                                sent.Content.Tile.TileId,
                                sent.Content.Tile.ParentStableId,
                                GetAncestorStableIds(facts, sent.Content.Tile.ParentStableId),
                                sent.NodeIds,
                                sent.Content.AssetCopyright));
                    }

                    fact.AssetCopyright = sent.Content.AssetCopyright;
                    if (sent.Succeeded || dryRun)
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
                        failedTiles += System.Math.Max(1, removed.FailedNodeCount);
                        _ = writerState.FailedRemovalStableIds.Add(removed.StableId);
                        if (removed.RemainingNodeIds.Count > 0 &&
                            writerState.VisibleTiles.TryGetValue(removed.StableId, out RetainedTileState? visibleTile))
                        {
                            writerState.VisibleTiles[removed.StableId] = visibleTile with
                            {
                                NodeIds = removed.RemainingNodeIds
                            };
                        }
                    }

                    break;

                case CleanupTileCompleted cleanup:
                    writerState.InFlightRemoveStableId = null;
                    if (cleanup.Succeeded)
                    {
                        _ = writerState.FailedCleanupStableIds.Remove(cleanup.StableId);
                        _ = writerState.CleanupDebtTiles.Remove(cleanup.StableId);
                    }
                    else
                    {
                        failedTiles += System.Math.Max(1, cleanup.FailedNodeCount);
                        _ = writerState.FailedCleanupStableIds.Add(cleanup.StableId);
                        if (cleanup.RemainingNodeIds.Count > 0 &&
                            writerState.CleanupDebtTiles.TryGetValue(cleanup.StableId, out RetainedTileState? cleanupTile))
                        {
                            writerState.CleanupDebtTiles[cleanup.StableId] = cleanupTile with
                            {
                                NodeIds = cleanup.RemainingNodeIds
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
            }
        }

        private static RetainedTileState MergeRetainedTileState(
            Dictionary<string, RetainedTileState> existingTiles,
            string stableId,
            RetainedTileState newTile)
        {
            if (!existingTiles.TryGetValue(stableId, out RetainedTileState? existing))
            {
                return newTile;
            }

            return newTile with
            {
                NodeIds = [.. existing.NodeIds.Concat(newTile.NodeIds).Distinct(StringComparer.Ordinal)]
            };
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

            if (writerState.CleanupDebtTiles.Keys.Any(stableId => !writerState.FailedCleanupStableIds.Contains(stableId)))
            {
                return false;
            }

            if (dryRun)
            {
                return true;
            }

            DesiredMetadataState desiredMetadata = BuildDesiredMetadataState(
                facts,
                writerState,
                desiredView,
                progress,
                DateTimeOffset.UtcNow);

            return string.Equals(desiredMetadata.LicenseCredit, writerState.AppliedLicenseCredit, StringComparison.Ordinal) &&
                   string.Equals(desiredMetadata.ProgressText, writerState.AppliedProgressText, StringComparison.Ordinal) &&
                   System.Math.Abs(desiredMetadata.ProgressValue - writerState.AppliedProgressValue) <= 0.0001f;
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
            DateTimeOffset now)
        {
            if (writerState.InFlightRemoveStableId is not null)
            {
                return null;
            }

            DesiredMetadataState? desiredMetadata = null;
            if (!dryRun && allowMetadata && !writerState.MetadataInFlight)
            {
                desiredMetadata = BuildDesiredMetadataState(
                    facts,
                    writerState,
                    desiredView,
                    progress,
                    now);
            }

            if (allowRemoval)
            {
                RetainedTileState? cleanup = writerState.CleanupDebtTiles.Values
                    .Where(tile => !writerState.FailedCleanupStableIds.Contains(tile.StableId))
                    .OrderByDescending(static tile => tile.AncestorStableIds.Count)
                    .ThenBy(static tile => tile.TileId, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (cleanup is not null)
                {
                    return new CleanupTileWriterCommand(cleanup.StableId, cleanup.TileId, cleanup.NodeIds);
                }

                RetainedTileState? removal = writerState.VisibleTiles.Values
                    .Where(tile => desiredView.SelectedStableIds.Contains(tile.StableId))
                    .Where(tile => !desiredView.StableIds.Contains(tile.StableId))
                    .Where(tile => !writerState.FailedRemovalStableIds.Contains(tile.StableId))
                    .Where(tile => !HasInFlightSendDescendant(
                        facts,
                        writerState,
                        tile.StableId))
                    .OrderByDescending(static tile => tile.AncestorStableIds.Count)
                    .ThenBy(static tile => tile.TileId, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (removal is not null)
                {
                    return new RemoveTileWriterCommand(removal.StableId, removal.TileId, removal.NodeIds);
                }
            }

            if (allowSend)
            {
                TileBranchFact? sendFact = desiredView.StableIds
                    .Where(stableId => !writerState.VisibleTiles.ContainsKey(stableId))
                    .Where(stableId => !writerState.InFlightSendStableIds.Contains(stableId))
                    .Where(stableId => facts.Branches.TryGetValue(stableId, out TileBranchFact? fact) &&
                                       fact.PreparedContent is not null &&
                                       fact.PrepareStatus == ContentDiscoveryStatus.Ready)
                    .Select(stableId => facts.Branches[stableId])
                    .OrderBy(static fact => fact.Tile.Depth)
                    .ThenBy(static fact => fact.Tile.DistanceToReferenceM ?? double.MaxValue)
                    .ThenByDescending(static fact => fact.Tile.HorizontalSpanM ?? double.MinValue)
                    .ThenBy(static fact => fact.PreparedOrder)
                    .FirstOrDefault();

                if (sendFact?.PreparedContent is not null)
                {
                    return new SendTileWriterCommand(sendFact.PreparedContent);
                }
            }

            if (desiredMetadata?.ShouldSync == true)
            {
                return new SyncSessionMetadataWriterCommand(
                    desiredMetadata.LicenseCredit,
                    desiredMetadata.ProgressValue,
                    desiredMetadata.ProgressText,
                    progress.ProcessedTiles,
                    desiredMetadata.UpdateLicense,
                    desiredMetadata.UpdateProgressText);
            }

            return null;
        }

        private static bool HasPendingRemovals(WriterState writerState, DesiredView desiredView)
            => CountPendingRemovals(writerState, desiredView) > 0;

        private static WriterReductionState BuildWriterReductionState(
            WriterState writerState,
            DesiredView desiredView,
            int maxConcurrentWriterSends)
        {
            WriterState planningState = writerState.CreatePlanningCopy();
            bool hasPendingRemovals = HasPendingRemovals(planningState, desiredView);
            int sendConcurrencyLimit = hasPendingRemovals
                ? System.Math.Max(1, maxConcurrentWriterSends - 1)
                : maxConcurrentWriterSends;
            return new WriterReductionState(planningState, hasPendingRemovals, sendConcurrencyLimit);
        }

        private static int CountPendingRemovals(WriterState writerState, DesiredView desiredView)
        {
            return writerState.CleanupDebtTiles.Values.Count(tile =>
                       !writerState.FailedCleanupStableIds.Contains(tile.StableId)) +
                   writerState.VisibleTiles.Values.Count(tile =>
                desiredView.SelectedStableIds.Contains(tile.StableId) &&
                !desiredView.StableIds.Contains(tile.StableId) &&
                !writerState.FailedRemovalStableIds.Contains(tile.StableId));
        }

        private static bool HasInFlightSendDescendant(
            DiscoveryFacts facts,
            WriterState writerState,
            string stableId)
        {
            foreach (string candidateStableId in writerState.InFlightSendStableIds)
            {
                if (string.Equals(candidateStableId, stableId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsDescendantOf(facts, candidateStableId, stableId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDescendantOf(DiscoveryFacts facts, string stableId, string ancestorStableId)
        {
            if (!facts.Branches.TryGetValue(stableId, out TileBranchFact? fact))
            {
                return false;
            }

            string? currentStableId = fact.Tile.ParentStableId;
            while (!string.IsNullOrWhiteSpace(currentStableId))
            {
                if (string.Equals(currentStableId, ancestorStableId, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!facts.Branches.TryGetValue(currentStableId, out TileBranchFact? parentFact))
                {
                    break;
                }

                currentStableId = parentFact.Tile.ParentStableId;
            }

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

        private DesiredMetadataState BuildDesiredMetadataState(
            DiscoveryFacts facts,
            WriterState writerState,
            DesiredView desiredView,
            ProgressSnapshot progress,
            DateTimeOffset now)
        {
            string desiredLicense = BuildDesiredLicense(writerState.VisibleTiles.Values.Concat(writerState.CleanupDebtTiles.Values));
            WriterBacklogState backlog = BuildWriterBacklogState(facts, writerState, desiredView, progress);
            float progressValue = backlog.PendingUnits == 0 || backlog.TotalUnits == 0
                ? 1f
                : System.Math.Clamp((float)progress.ProcessedTiles / backlog.TotalUnits, 0f, 1f);
            bool previouslyCompleted = IsCompletedMetadata(writerState.AppliedProgressValue, writerState.AppliedProgressText);
            if (writerState.AppliedProgressValue >= 0f &&
                (!previouslyCompleted || backlog.PendingUnits == 0))
            {
                progressValue = System.Math.Max(progressValue, writerState.AppliedProgressValue);
            }
            string progressText = backlog.PendingUnits == 0
                ? $"Completed: candidate={progress.CandidateTiles} processed={progress.ProcessedTiles} streamed={progress.StreamedMeshes} failed={progress.FailedTiles}"
                : "Running...";
            bool updateLicense = !string.Equals(desiredLicense, writerState.AppliedLicenseCredit, StringComparison.Ordinal);
            bool updateProgressText = !string.Equals(progressText, writerState.AppliedProgressText, StringComparison.Ordinal);
            bool progressValueChanged = System.Math.Abs(progressValue - writerState.AppliedProgressValue) > 0.0001f;
            bool isCompleted = IsCompletedMetadata(progressValue, progressText);
            bool completionStateChanged = previouslyCompleted != isCompleted;
            bool cadenceElapsed = now - writerState.LastMetadataSyncStartedAt >= MetadataCadence;
            bool processedDeltaReached = progress.ProcessedTiles - writerState.LastMetadataSyncProcessedTiles >= MetadataProcessedDeltaThreshold;
            bool progressDeltaReached = writerState.LastMetadataSyncProgressValue < 0f ||
                progressValue - writerState.LastMetadataSyncProgressValue >= MetadataProgressDeltaThreshold;

            return new DesiredMetadataState(
                desiredLicense,
                progressValue,
                progressText,
                updateLicense,
                updateProgressText,
                progressValueChanged,
                isCompleted,
                completionStateChanged,
                cadenceElapsed,
                processedDeltaReached,
                progressDeltaReached,
                backlog.IsQuiescent);
        }

        private static WriterBacklogState BuildWriterBacklogState(
            DiscoveryFacts facts,
            WriterState writerState,
            DesiredView desiredView,
            ProgressSnapshot progress)
        {
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
            int pendingRemove = CountPendingRemovals(writerState, desiredView);
            return new WriterBacklogState(
                pendingDiscovery,
                pendingPrepared,
                pendingSend,
                pendingRemove,
                writerState.InFlightSendStableIds.Count,
                writerState.InFlightRemoveStableId is not null,
                writerState.MetadataInFlight,
                progress.CandidateTiles,
                progress.ProcessedTiles);
        }

        private static void ApplyPlanningInFlight(WriterState writerState, WriterCommand command, DateTimeOffset now)
        {
            switch (command)
            {
                case SendTileWriterCommand send:
                    _ = writerState.InFlightSendStableIds.Add(send.Content.Tile.StableId!);
                    break;
                case RemoveTileWriterCommand remove:
                    writerState.InFlightRemoveStableId = remove.StableId;
                    break;
                case CleanupTileWriterCommand cleanup:
                    writerState.InFlightRemoveStableId = cleanup.StableId;
                    break;
                case SyncSessionMetadataWriterCommand metadata:
                    writerState.MetadataInFlight = true;
                    writerState.LastMetadataSyncStartedAt = now;
                    writerState.LastMetadataSyncProcessedTiles = metadata.ProcessedTiles;
                    writerState.LastMetadataSyncProgressValue = metadata.ProgressValue;
                    break;
            }
        }

        private static bool IsCompletedMetadata(float progressValue, string progressText)
            => progressValue >= 0.9999f || progressText.StartsWith("Completed:", StringComparison.Ordinal);

        private string BuildDesiredLicense(IEnumerable<RetainedTileState> visibleTiles)
        {
            var aggregator = new LicenseCreditAggregator(_licenseCreditPolicy);
            foreach (RetainedTileState retainedTile in visibleTiles)
            {
                IReadOnlyList<string> owners = aggregator.ParseOwners(
                    string.IsNullOrWhiteSpace(retainedTile.AssetCopyright) ? [] : [retainedTile.AssetCopyright]);
                aggregator.RegisterOrder(owners);
                _ = aggregator.Activate(owners);
            }

            string built = aggregator.BuildCreditString();
            return string.IsNullOrWhiteSpace(built) ? _licenseCreditPolicy.DefaultCredit : built;
        }

        public SceneReconcilerCore()
            : this(new Google.GoogleTileLicenseCreditPolicy())
        {
        }
    }
}
