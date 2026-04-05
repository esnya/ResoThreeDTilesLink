using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

#pragma warning disable CA1822

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

        public string? InFlightSendStableId { get; set; }

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

    public sealed class TraversalCore(ITileSelector selector)
    {
        private static readonly TimeSpan VisibleReplacementGrace = TimeSpan.FromSeconds(1);

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

        internal DesiredView ComputeDesiredView(DiscoveryFacts facts, WriterState writerState)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);

            HashSet<string> selectedStableIds = facts.Branches.Keys.ToHashSet(StringComparer.Ordinal);
            IReadOnlyCollection<RetainedTileState> planningVisibleTiles = GetPlanningVisibleTiles(facts, writerState);
            HashSet<string> candidateStableIds = GetCandidateStableIds(facts, planningVisibleTiles);
            HashSet<string> ancestorsWithOutOfSelectionVisibleDescendants = BuildAncestorsWithVisibleDescendants(
                planningVisibleTiles.Where(tile => !selectedStableIds.Contains(tile.StableId)));
            Dictionary<string, List<string>> childrenByParent = BuildChildrenByParent(facts);
            var branchHasCandidatesMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
            var subtreeCoveredMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
            var desired = new HashSet<string>(StringComparer.Ordinal);

            foreach (string stableId in candidateStableIds)
            {
                if (!facts.Branches.TryGetValue(stableId, out TileBranchFact? fact))
                {
                    continue;
                }

                if (!IsRenderableAvailable(fact, writerState))
                {
                    continue;
                }

                if (ancestorsWithOutOfSelectionVisibleDescendants.Contains(stableId))
                {
                    continue;
                }

                if (CanHideBehindCoveredChildren(
                        stableId,
                        facts,
                        writerState,
                        childrenByParent,
                        candidateStableIds,
                        branchHasCandidatesMemo,
                        subtreeCoveredMemo))
                {
                    continue;
                }

                _ = desired.Add(stableId);
            }

            return new DesiredView(desired, selectedStableIds, candidateStableIds);
        }

        internal IReadOnlyList<DiscoveryWorkItem> PlanDiscovery(
            DiscoveryFacts facts,
            WriterState writerState,
            int availableSlots)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            ArgumentOutOfRangeException.ThrowIfNegative(availableSlots);

            if (availableSlots == 0)
            {
                return [];
            }

            IReadOnlyCollection<RetainedTileState> planningVisibleTiles = GetPlanningVisibleTiles(facts, writerState);
            HashSet<string> candidateStableIds = GetCandidateStableIds(facts, planningVisibleTiles);
            HashSet<string> ancestorsWithVisibleDescendants = BuildAncestorsWithVisibleDescendants(planningVisibleTiles);
            List<DiscoveryWorkItem> planned = [];

            foreach (TileBranchFact fact in facts.Branches.Values
                         .Select(branch => new
                         {
                             Fact = branch,
                             HasVisibleAncestor = HasVisibleAncestor(facts, writerState, branch.Tile.ParentStableId),
                             NeedsCoverage = !HasVisibleAncestor(facts, writerState, branch.Tile.ParentStableId) &&
                                 ShouldPrioritizeCoverage(facts.Request, branch.Tile)
                         })
                         .OrderBy(static entry => entry.NeedsCoverage ? 0 : 1)
                         .ThenBy(entry => entry.NeedsCoverage ? entry.Fact.Tile.Depth : -entry.Fact.Tile.Depth)
                         .ThenBy(static entry => entry.Fact.Tile.HorizontalSpanM ?? double.MaxValue)
                         .ThenBy(static entry => entry.Fact.Tile.TileId, StringComparer.Ordinal)
                         .Select(static entry => entry.Fact))
            {
                if (planned.Count >= availableSlots)
                {
                    break;
                }

                switch (fact.Tile.ContentKind)
                {
                    case TileContentKind.Json:
                        if (fact.Tile.Depth < facts.Request.Traversal.MaxDepth &&
                            fact.NestedStatus == ContentDiscoveryStatus.Unrequested)
                        {
                            planned.Add(new LoadNestedTilesetWorkItem(fact.Tile));
                        }

                        break;

                    case TileContentKind.Glb:
                        if (!candidateStableIds.Contains(fact.Tile.StableId!))
                        {
                            break;
                        }

                        if (writerState.VisibleTiles.ContainsKey(fact.Tile.StableId!))
                        {
                            break;
                        }

                        if (ancestorsWithVisibleDescendants.Contains(fact.Tile.StableId!))
                        {
                            break;
                        }

                        if (fact.PrepareStatus == ContentDiscoveryStatus.Unrequested)
                        {
                            planned.Add(new PrepareTileWorkItem(fact.Tile));
                        }

                        break;
                }
            }

            return planned;
        }

        internal WriterCommand? PlanWriterCommand(
            DiscoveryFacts facts,
            WriterState writerState,
            DesiredView desiredView,
            ProgressSnapshot progress,
            bool dryRun,
            DateTimeOffset? now = null)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            ArgumentNullException.ThrowIfNull(desiredView);
            ArgumentNullException.ThrowIfNull(progress);

            DateTimeOffset currentTime = now ?? DateTimeOffset.UtcNow;

            if (writerState.InFlightSendStableId is not null ||
                writerState.InFlightRemoveStableId is not null ||
                writerState.MetadataInFlight)
            {
                return null;
            }

            DateTimeOffset? deferredRemovalDueAt = null;
            RetainedTileState? removal = writerState.VisibleTiles.Values
                .Where(tile => desiredView.SelectedStableIds.Contains(tile.StableId))
                .Where(tile => !desiredView.StableIds.Contains(tile.StableId))
                .Where(tile => !writerState.FailedRemovalStableIds.Contains(tile.StableId))
                .Where(tile => HasVisibleDescendant(tile.StableId, writerState.VisibleTiles.Values))
                .Select(tile => new
                {
                    Tile = tile,
                    ReadyAt = GetReplacementReadyAt(tile.StableId, writerState)
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

            TileBranchFact? coverageFact = facts.Branches.Values
                .Where(static fact => fact.Tile.ContentKind == TileContentKind.Glb)
                .Where(fact => desiredView.CandidateStableIds.Contains(fact.Tile.StableId!))
                .Where(fact => !writerState.VisibleTiles.ContainsKey(fact.Tile.StableId!))
                .Where(fact => fact.PreparedContent is not null &&
                               fact.PrepareStatus == ContentDiscoveryStatus.Ready)
                .Where(fact => !HasVisibleAncestor(facts, writerState, fact.Tile.ParentStableId))
                .Where(fact => ShouldPrioritizeCoverage(facts.Request, fact.Tile))
                .OrderBy(static fact => fact.Tile.Depth)
                .ThenByDescending(static fact => fact.Tile.HorizontalSpanM ?? double.MinValue)
                .ThenBy(static fact => fact.PreparedOrder)
                .FirstOrDefault();

            if (coverageFact?.PreparedContent is not null)
            {
                return new SendTileWriterCommand(coverageFact.PreparedContent);
            }

            TileBranchFact? sendFact = desiredView.StableIds
                .Where(stableId => !writerState.VisibleTiles.ContainsKey(stableId))
                .Where(stableId => facts.Branches.TryGetValue(stableId, out TileBranchFact? fact) &&
                                   fact.PreparedContent is not null &&
                                   fact.PrepareStatus == ContentDiscoveryStatus.Ready)
                .Select(stableId => facts.Branches[stableId])
                .OrderBy(fact => GetNearestVisibleAncestorDepth(facts, writerState, fact.Tile.ParentStableId))
                .ThenBy(static fact => fact.Tile.Depth)
                .ThenByDescending(static fact => fact.Tile.HorizontalSpanM ?? double.MinValue)
                .ThenBy(static fact => fact.PreparedOrder)
                .FirstOrDefault();

            if (sendFact?.PreparedContent is not null)
            {
                return new SendTileWriterCommand(sendFact.PreparedContent);
            }

            if (!dryRun)
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

        internal void ApplyWriterCompletion(
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
                    writerState.InFlightSendStableId = null;
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

                    // Partial sends are retained only when rollback failed; fully rolled back sends are retried.
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

        internal bool IsSettled(
            DiscoveryFacts facts,
            WriterState writerState,
            DesiredView desiredView,
            int inFlightDiscoveryCount,
            bool writerBusy,
            ProgressSnapshot progress,
            bool dryRun)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            ArgumentNullException.ThrowIfNull(desiredView);
            ArgumentNullException.ThrowIfNull(progress);

            if (inFlightDiscoveryCount != 0 || writerBusy)
            {
                return false;
            }

            if (PlanDiscovery(facts, writerState, availableSlots: int.MaxValue).Count != 0)
            {
                return false;
            }

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

        internal IReadOnlyDictionary<string, RetainedTileState> BuildVisibleTiles(WriterState writerState)
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

        internal int CountCandidateTiles(DiscoveryFacts facts, WriterState writerState)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(writerState);
            return GetCandidateStableIds(facts, GetPlanningVisibleTiles(facts, writerState)).Count;
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

        private static Dictionary<string, List<string>> BuildChildrenByParent(DiscoveryFacts facts)
        {
            var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (TileBranchFact fact in facts.Branches.Values)
            {
                if (string.IsNullOrWhiteSpace(fact.Tile.ParentStableId))
                {
                    continue;
                }

                if (!childrenByParent.TryGetValue(fact.Tile.ParentStableId, out List<string>? children))
                {
                    children = [];
                    childrenByParent.Add(fact.Tile.ParentStableId, children);
                }

                children.Add(fact.Tile.StableId!);
            }

            return childrenByParent;
        }

        private static bool HasPreparedDescendant(
            string stableId,
            DiscoveryFacts facts,
            Dictionary<string, List<string>> childrenByParent,
            IReadOnlySet<string> candidateStableIds,
            Dictionary<string, bool> memo)
        {
            if (memo.TryGetValue(stableId, out bool cached))
            {
                return cached;
            }

            if (!childrenByParent.TryGetValue(stableId, out List<string>? children))
            {
                memo[stableId] = false;
                return false;
            }

            foreach (string childId in children)
            {
                if (!facts.Branches.TryGetValue(childId, out TileBranchFact? child))
                {
                    continue;
                }

                if (candidateStableIds.Contains(childId) &&
                    child.PrepareStatus == ContentDiscoveryStatus.Ready)
                {
                    memo[stableId] = true;
                    return true;
                }

                if (HasPreparedDescendant(childId, facts, childrenByParent, candidateStableIds, memo))
                {
                    memo[stableId] = true;
                    return true;
                }
            }

            memo[stableId] = false;
            return false;
        }

        private static bool CanHideBehindCoveredChildren(
            string stableId,
            DiscoveryFacts facts,
            WriterState writerState,
            Dictionary<string, List<string>> childrenByParent,
            IReadOnlySet<string> candidateStableIds,
            Dictionary<string, bool> branchHasCandidatesMemo,
            Dictionary<string, bool> subtreeCoveredMemo)
        {
            if (facts.Branches.TryGetValue(stableId, out TileBranchFact? fact) &&
                !writerState.VisibleTiles.ContainsKey(stableId) &&
                !string.IsNullOrWhiteSpace(fact.Tile.ParentStableId) &&
                !candidateStableIds.Contains(fact.Tile.ParentStableId) &&
                !writerState.VisibleTiles.ContainsKey(fact.Tile.ParentStableId))
            {
                return false;
            }

            if (!childrenByParent.TryGetValue(stableId, out List<string>? children))
            {
                return false;
            }

            bool hasRelevantChild = false;
            foreach (string childId in children)
            {
                if (!BranchHasCandidate(childId, childrenByParent, candidateStableIds, branchHasCandidatesMemo))
                {
                    continue;
                }

                hasRelevantChild = true;
                if (!IsSubtreeCovered(
                        childId,
                        facts,
                        writerState,
                        childrenByParent,
                        candidateStableIds,
                        branchHasCandidatesMemo,
                        subtreeCoveredMemo))
                {
                    return false;
                }
            }

            return hasRelevantChild;
        }

        private static bool BranchHasCandidate(
            string stableId,
            Dictionary<string, List<string>> childrenByParent,
            IReadOnlySet<string> candidateStableIds,
            Dictionary<string, bool> memo)
        {
            if (memo.TryGetValue(stableId, out bool cached))
            {
                return cached;
            }

            if (candidateStableIds.Contains(stableId))
            {
                memo[stableId] = true;
                return true;
            }

            if (!childrenByParent.TryGetValue(stableId, out List<string>? children))
            {
                memo[stableId] = false;
                return false;
            }

            foreach (string childId in children)
            {
                if (BranchHasCandidate(childId, childrenByParent, candidateStableIds, memo))
                {
                    memo[stableId] = true;
                    return true;
                }
            }

            memo[stableId] = false;
            return false;
        }

        private static bool IsSubtreeCovered(
            string stableId,
            DiscoveryFacts facts,
            WriterState writerState,
            Dictionary<string, List<string>> childrenByParent,
            IReadOnlySet<string> candidateStableIds,
            Dictionary<string, bool> branchHasCandidatesMemo,
            Dictionary<string, bool> memo)
        {
            if (memo.TryGetValue(stableId, out bool cached))
            {
                return cached;
            }

            if (facts.Branches.TryGetValue(stableId, out TileBranchFact? fact) &&
                candidateStableIds.Contains(stableId) &&
                IsRenderableAvailable(fact, writerState))
            {
                memo[stableId] = true;
                return true;
            }

            if (!childrenByParent.TryGetValue(stableId, out List<string>? children))
            {
                memo[stableId] = false;
                return false;
            }

            bool hasRelevantChild = false;
            foreach (string childId in children)
            {
                if (!BranchHasCandidate(childId, childrenByParent, candidateStableIds, branchHasCandidatesMemo))
                {
                    continue;
                }

                hasRelevantChild = true;
                if (!IsSubtreeCovered(
                        childId,
                        facts,
                        writerState,
                        childrenByParent,
                        candidateStableIds,
                        branchHasCandidatesMemo,
                        memo))
                {
                    memo[stableId] = false;
                    return false;
                }
            }

            memo[stableId] = hasRelevantChild;
            return hasRelevantChild;
        }

        private static bool IsRenderableAvailable(TileBranchFact fact, WriterState writerState)
        {
            if (fact.SendFailed &&
                !writerState.VisibleTiles.ContainsKey(fact.Tile.StableId!))
            {
                return false;
            }

            return writerState.VisibleTiles.ContainsKey(fact.Tile.StableId!) ||
                   (fact.PrepareStatus == ContentDiscoveryStatus.Ready && fact.PreparedContent is not null);
        }

        private static IReadOnlyCollection<RetainedTileState> GetPlanningVisibleTiles(
            DiscoveryFacts facts,
            WriterState writerState)
        {
            if (!facts.RemoveOutOfRangeRetainedTiles)
            {
                return writerState.VisibleTiles.Values.ToArray();
            }

            return writerState.VisibleTiles.Values
                .Where(tile => facts.Branches.ContainsKey(tile.StableId))
                .ToArray();
        }

        private static HashSet<string> GetCandidateStableIds(
            DiscoveryFacts facts,
            IReadOnlyCollection<RetainedTileState> planningVisibleTiles)
        {
            HashSet<string> visibleStableIds = planningVisibleTiles
                .Select(static tile => tile.StableId)
                .ToHashSet(StringComparer.Ordinal);

            IEnumerable<TileBranchFact> prioritized = visibleStableIds.Count == 0
                ? facts.Branches.Values
                    .Where(static fact => fact.Tile.ContentKind == TileContentKind.Glb)
                    .OrderByDescending(static fact => fact.Tile.Depth)
                    .ThenBy(static fact => fact.Tile.HorizontalSpanM ?? double.MaxValue)
                    .ThenBy(static fact => fact.Tile.TileId, StringComparer.Ordinal)
                : GetVisibleFrontierCandidates(
                    facts,
                    visibleStableIds,
                    BuildAncestorsWithVisibleDescendants(planningVisibleTiles));

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (TileBranchFact fact in prioritized)
            {
                if (candidates.Count >= facts.Request.Traversal.MaxTiles &&
                    !visibleStableIds.Contains(fact.Tile.StableId!))
                {
                    continue;
                }

                _ = candidates.Add(fact.Tile.StableId!);
            }

            if (visibleStableIds.Count == 0)
            {
                foreach (string stableId in facts.Branches.Values
                             .Where(static fact => fact.Tile.ContentKind == TileContentKind.Glb)
                             .Where(fact => ShouldPrioritizeCoverage(facts.Request, fact.Tile))
                             .OrderBy(static fact => fact.Tile.Depth)
                             .ThenByDescending(static fact => fact.Tile.HorizontalSpanM ?? double.MinValue)
                             .ThenBy(static fact => fact.Tile.TileId, StringComparer.Ordinal)
                             .Select(static fact => fact.Tile.StableId!))
                {
                    _ = candidates.Add(stableId);
                }
            }

            foreach (string visibleStableId in visibleStableIds)
            {
                if (facts.Branches.ContainsKey(visibleStableId))
                {
                    _ = candidates.Add(visibleStableId);
                }
            }

            return candidates;
        }

        private static IReadOnlyList<TileBranchFact> GetVisibleFrontierCandidates(
            DiscoveryFacts facts,
            IReadOnlySet<string> visibleStableIds,
            IReadOnlySet<string> ancestorsWithVisibleDescendants)
        {
            Dictionary<string, List<string>> childrenByParent = BuildChildrenByParent(facts);
            var frontier = new List<TileBranchFact>();
            var queue = new Queue<string>(
                facts.Branches.Values
                    .Where(fact => string.IsNullOrWhiteSpace(fact.Tile.ParentStableId) ||
                                   !facts.Branches.ContainsKey(fact.Tile.ParentStableId))
                    .OrderBy(static fact => fact.Tile.Depth)
                    .ThenByDescending(static fact => fact.Tile.HorizontalSpanM ?? double.MinValue)
                    .ThenBy(static fact => fact.Tile.TileId, StringComparer.Ordinal)
                    .Select(static fact => fact.Tile.StableId!));

            while (queue.Count > 0)
            {
                string stableId = queue.Dequeue();
                if (!facts.Branches.TryGetValue(stableId, out TileBranchFact? fact))
                {
                    continue;
                }

                if (fact.Tile.ContentKind == TileContentKind.Glb &&
                    !visibleStableIds.Contains(stableId) &&
                    !ancestorsWithVisibleDescendants.Contains(stableId))
                {
                    frontier.Add(fact);
                    continue;
                }

                if (!childrenByParent.TryGetValue(stableId, out List<string>? children))
                {
                    continue;
                }

                foreach (string childId in children
                             .Where(facts.Branches.ContainsKey)
                             .OrderBy(childId => facts.Branches[childId].Tile.Depth)
                             .ThenByDescending(childId => facts.Branches[childId].Tile.HorizontalSpanM ?? double.MinValue)
                             .ThenBy(childId => facts.Branches[childId].Tile.TileId, StringComparer.Ordinal))
                {
                    queue.Enqueue(childId);
                }
            }

            return frontier
                .OrderByDescending(fact => HasVisibleAncestor(visibleStableIds, facts, fact.Tile.ParentStableId))
                .ThenBy(static fact => fact.Tile.Depth)
                .ThenByDescending(static fact => fact.Tile.HorizontalSpanM ?? double.MinValue)
                .ThenBy(static fact => fact.Tile.TileId, StringComparer.Ordinal)
                .ToArray();
        }

        private static HashSet<string> BuildAncestorsWithVisibleDescendants(IEnumerable<RetainedTileState> visibleTiles)
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            foreach (RetainedTileState tile in visibleTiles)
            {
                foreach (string ancestor in tile.AncestorStableIds)
                {
                    _ = ancestors.Add(ancestor);
                }
            }

            return ancestors;
        }

        private static bool HasVisibleDescendant(string stableId, IEnumerable<RetainedTileState> visibleTiles)
        {
            foreach (RetainedTileState tile in visibleTiles)
            {
                if (tile.AncestorStableIds.Contains(stableId, StringComparer.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static DateTimeOffset? GetReplacementReadyAt(string stableId, WriterState writerState)
        {
            DateTimeOffset? earliest = null;
            foreach (RetainedTileState tile in writerState.VisibleTiles.Values)
            {
                if (!tile.AncestorStableIds.Contains(stableId, StringComparer.Ordinal))
                {
                    continue;
                }

                DateTimeOffset visibleSince = writerState.VisibleSinceByStableId.TryGetValue(tile.StableId, out DateTimeOffset timestamp)
                    ? timestamp
                    : DateTimeOffset.MinValue;
                DateTimeOffset readyAt = visibleSince == DateTimeOffset.MinValue
                    ? DateTimeOffset.MinValue
                    : visibleSince + VisibleReplacementGrace;

                if (earliest is null || readyAt < earliest)
                {
                    earliest = readyAt;
                }
            }

            return earliest;
        }

        private static bool HasVisibleAncestor(
            DiscoveryFacts facts,
            WriterState writerState,
            string? parentStableId)
        {
            return HasVisibleAncestor(writerState.VisibleTiles.Keys.ToHashSet(StringComparer.Ordinal), facts, parentStableId);
        }

        private static bool HasVisibleAncestor(
            IReadOnlySet<string> visibleStableIds,
            DiscoveryFacts facts,
            string? parentStableId)
        {
            string? currentId = parentStableId;
            while (!string.IsNullOrWhiteSpace(currentId))
            {
                if (visibleStableIds.Contains(currentId))
                {
                    return true;
                }

                if (!facts.Branches.TryGetValue(currentId, out TileBranchFact? parent))
                {
                    return false;
                }

                currentId = parent.Tile.ParentStableId;
            }

            return false;
        }

        private static int GetNearestVisibleAncestorDepth(
            DiscoveryFacts facts,
            WriterState writerState,
            string? parentStableId)
        {
            string? currentId = parentStableId;
            while (!string.IsNullOrWhiteSpace(currentId))
            {
                if (writerState.VisibleTiles.ContainsKey(currentId))
                {
                    return facts.Branches.TryGetValue(currentId, out TileBranchFact? fact)
                        ? fact.Tile.Depth
                        : int.MaxValue;
                }

                if (!facts.Branches.TryGetValue(currentId, out TileBranchFact? parent))
                {
                    break;
                }

                currentId = parent.Tile.ParentStableId;
            }

            return int.MaxValue;
        }

        private static bool ShouldPrioritizeCoverage(TileRunRequest request, TileSelectionResult tile)
        {
            return tile.HasChildren &&
                   tile.HorizontalSpanM is double span &&
                   span > request.Traversal.RangeM *
                   (request.Traversal.BootstrapRangeMultiplier > 0d
                       ? request.Traversal.BootstrapRangeMultiplier
                       : 4d);
        }

        private static IReadOnlyList<string> GetAncestorStableIds(DiscoveryFacts facts, string? parentStableId)
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
            int pendingSend = desiredView.StableIds.Count(stableId => !writerState.VisibleTiles.ContainsKey(stableId));
            int pendingRemove = writerState.VisibleTiles.Values.Count(tile =>
                desiredView.SelectedStableIds.Contains(tile.StableId) &&
                !desiredView.StableIds.Contains(tile.StableId) &&
                !writerState.FailedRemovalStableIds.Contains(tile.StableId) &&
                HasVisibleDescendant(tile.StableId, writerState.VisibleTiles.Values));

            int completedUnits = progress.ProcessedTiles;
            int pendingUnits = pendingDiscovery + pendingSend + pendingRemove +
                (writerState.InFlightSendStableId is null ? 0 : 1) +
                (writerState.InFlightRemoveStableId is null ? 0 : 1) +
                (writerState.MetadataInFlight ? 1 : 0);
            int totalUnits = completedUnits + pendingUnits;
            float progressValue = totalUnits == 0
                ? 1f
                : System.Math.Clamp((float)completedUnits / totalUnits, 0f, 1f);
            string progressText = pendingUnits == 0
                ? $"Completed: candidate={progress.CandidateTiles} processed={progress.ProcessedTiles} streamed={progress.StreamedMeshes} failed={progress.FailedTiles}"
                : $"Running: candidate={progress.CandidateTiles} processed={progress.ProcessedTiles} streamed={progress.StreamedMeshes} failed={progress.FailedTiles} queued-send={pendingSend}";

            return (desiredLicense, progressValue, progressText);
        }

        private static string BuildDesiredLicense(IEnumerable<RetainedTileState> visibleTiles)
        {
            var aggregator = new LicenseCreditAggregator();
            foreach (RetainedTileState retainedTile in visibleTiles)
            {
                IReadOnlyList<string> owners = LicenseCreditAggregator.ParseOwners(
                    string.IsNullOrWhiteSpace(retainedTile.AssetCopyright)
                        ? []
                        : [retainedTile.AssetCopyright]);
                aggregator.RegisterOrder(owners);
                _ = aggregator.Activate(owners);
            }

            string built = aggregator.BuildCreditString();
            return string.IsNullOrWhiteSpace(built) ? "Google Maps" : built;
        }
    }
}
