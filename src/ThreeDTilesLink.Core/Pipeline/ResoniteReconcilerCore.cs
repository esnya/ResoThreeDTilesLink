using ThreeDTilesLink.Core.Models;

#pragma warning disable CA1822

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class ResoniteReconcilerCore
    {
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
            WriterState planningState = writerState.CreatePlanningCopy();
            bool hasPendingRemovals = HasPendingRemovals(planningState, desiredView);
            int sendConcurrencyLimit = hasPendingRemovals
                ? System.Math.Max(1, maxConcurrentWriterSends - 1)
                : maxConcurrentWriterSends;

            WriterCommand? controlCommand = null;
            if (planningState.InFlightRemoveStableId is null)
            {
                WriterCommand? nextControlCommand = PlanWriterCommand(
                    facts,
                    planningState,
                    desiredView,
                    progress,
                    dryRun,
                    allowRemoval: true,
                    allowSend: false,
                    allowMetadata: true,
                    planningTime);
                if (nextControlCommand is RemoveTileWriterCommand or SyncSessionMetadataWriterCommand)
                {
                    controlCommand = nextControlCommand;
                    ApplyPlanningInFlight(planningState, nextControlCommand, planningTime);
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
                    allowMetadata: false,
                    planningTime);
                if (sendCommand is not SendTileWriterCommand sendTileCommand)
                {
                    break;
                }

                sendCommands.Add(sendTileCommand);
                ApplyPlanningInFlight(planningState, sendTileCommand, planningTime);
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
                    bool partialSendRecoveryNeeded = !sent.Succeeded && sent.SlotIds.Count > 0;
                    bool shouldMarkVisible = sent.Succeeded || dryRun;

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
                        bool canRetrySendFailure = fact.CanRetryCompleteSendFailure;
                        fact.SendFailed = !string.IsNullOrWhiteSpace(fact.Tile.ParentStableId) &&
                            (!partialSendRecoveryNeeded || !canRetrySendFailure);
                        if (!canRetrySendFailure)
                        {
                            fact.PrepareStatus = ContentDiscoveryStatus.Failed;
                        }
                        else if (!partialSendRecoveryNeeded)
                        {
                            fact.PrepareStatus = ContentDiscoveryStatus.Ready;
                        }
                    }
                    else
                    {
                        fact.SendFailed = false;
                    }

                    if (shouldMarkVisible)
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
                    if (shouldMarkVisible)
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
            DateTimeOffset now)
        {
            if (writerState.InFlightRemoveStableId is not null)
            {
                return null;
            }

            bool metadataChanged = false;
            bool allowGeneralMetadata = false;
            string desiredLicense = string.Empty;
            float desiredProgressValue = 0f;
            string desiredProgressText = string.Empty;

            if (!dryRun && allowMetadata && !writerState.MetadataInFlight)
            {
                (desiredLicense, desiredProgressValue, desiredProgressText) = BuildDesiredMetadata(
                    facts,
                    writerState,
                    desiredView,
                    progress);
                bool licenseChanged = !string.Equals(desiredLicense, writerState.AppliedLicenseCredit, StringComparison.Ordinal);
                bool progressTextChanged = !string.Equals(desiredProgressText, writerState.AppliedProgressText, StringComparison.Ordinal);
                bool isCompleted = IsCompletedMetadata(desiredProgressValue, desiredProgressText);
                bool cadenceElapsed = now - writerState.LastMetadataSyncStartedAt >= MetadataCadence;
                bool processedDeltaReached = progress.ProcessedTiles - writerState.LastMetadataSyncProcessedTiles >= MetadataProcessedDeltaThreshold;
                bool progressDeltaReached = writerState.LastMetadataSyncProgressValue < 0f ||
                    desiredProgressValue - writerState.LastMetadataSyncProgressValue >= MetadataProgressDeltaThreshold;
                int pendingSend = desiredView.StableIds.Count(stableId => !writerState.VisibleTiles.ContainsKey(stableId));
                int pendingRemove = CountPendingRemovals(writerState, desiredView);
                bool quiescent = pendingSend == 0 &&
                    pendingRemove == 0 &&
                    writerState.InFlightSendStableIds.Count == 0;
                metadataChanged = licenseChanged ||
                    System.Math.Abs(desiredProgressValue - writerState.AppliedProgressValue) > 0.0001f ||
                    progressTextChanged;

                if (metadataChanged)
                {
                    allowGeneralMetadata = licenseChanged ||
                        isCompleted ||
                        quiescent ||
                        (cadenceElapsed && (processedDeltaReached || progressDeltaReached));
                }
            }

            if (allowRemoval)
            {
                RetainedTileState? removal = writerState.VisibleTiles.Values
                    .Where(tile => desiredView.SelectedStableIds.Contains(tile.StableId))
                    .Where(tile => !desiredView.StableIds.Contains(tile.StableId))
                    .Where(tile => !writerState.FailedRemovalStableIds.Contains(tile.StableId))
                    .OrderByDescending(static tile => tile.AncestorStableIds.Count)
                    .ThenBy(static tile => tile.TileId, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (removal is not null)
                {
                    return new RemoveTileWriterCommand(removal.StableId, removal.TileId, removal.SlotIds);
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

            if (allowGeneralMetadata)
            {
                bool updateLicense = !string.Equals(desiredLicense, writerState.AppliedLicenseCredit, StringComparison.Ordinal);
                bool updateProgressText = !string.Equals(desiredProgressText, writerState.AppliedProgressText, StringComparison.Ordinal);
                return new SyncSessionMetadataWriterCommand(
                    desiredLicense,
                    desiredProgressValue,
                    desiredProgressText,
                    progress.ProcessedTiles,
                    updateLicense,
                    updateProgressText);
            }

            return null;
        }

        private static bool HasPendingRemovals(WriterState writerState, DesiredView desiredView)
            => CountPendingRemovals(writerState, desiredView) > 0;

        private static int CountPendingRemovals(WriterState writerState, DesiredView desiredView)
        {
            return writerState.VisibleTiles.Values.Count(tile =>
                desiredView.SelectedStableIds.Contains(tile.StableId) &&
                !desiredView.StableIds.Contains(tile.StableId) &&
                !writerState.FailedRemovalStableIds.Contains(tile.StableId));
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

        private static (string LicenseCredit, float ProgressValue, string ProgressText) BuildDesiredMetadata(
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
            int pendingRemove = CountPendingRemovals(writerState, desiredView);

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
                : "Running...";

            return (desiredLicense, progressValue, progressText);
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
