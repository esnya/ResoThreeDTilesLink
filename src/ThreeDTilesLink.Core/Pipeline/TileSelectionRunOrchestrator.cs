using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TileSelectionRunOrchestrator(
        ITilesSource tilesSource,
        TraversalCore traversalCore,
        ResoniteReconcilerCore reconcilerCore,
        IContentProcessor contentProcessor,
        IMeshPlacementService meshPlacementService,
        IResoniteSession resoniteSession,
        IResoniteSessionMetadataPort sessionMetadataPort,
        ILogger logger,
        int maxConcurrentTileProcessing = 1,
        int maxConcurrentWriterSends = 1,
        RunPerformanceSummary? performanceSummary = null)
    {
        private readonly ITilesSource _tilesSource = tilesSource;
        private readonly TraversalCore _traversalCore = traversalCore;
        private readonly ResoniteReconcilerCore _reconcilerCore = reconcilerCore;
        private readonly IContentProcessor _contentProcessor = contentProcessor;
        private readonly IMeshPlacementService _meshPlacementService = meshPlacementService;
        private readonly IResoniteSession _resoniteSession = resoniteSession;
        private readonly IResoniteSessionMetadataPort _sessionMetadataPort = sessionMetadataPort;
        private readonly ILogger _logger = logger;
        private readonly int _maxConcurrentTileProcessing = maxConcurrentTileProcessing > 0
            ? maxConcurrentTileProcessing
            : throw new ArgumentOutOfRangeException(nameof(maxConcurrentTileProcessing), "Tile content worker count must be positive.");
        private readonly int _maxConcurrentWriterSends = maxConcurrentWriterSends > 0
            ? maxConcurrentWriterSends
            : throw new ArgumentOutOfRangeException(nameof(maxConcurrentWriterSends), "Writer send concurrency must be positive.");
        private readonly int _maxConcurrentNestedTilesetLoads = global::System.Math.Max(2, maxConcurrentTileProcessing);
        private readonly RunPerformanceSummary? _performanceSummary = performanceSummary;

        private static readonly Action<ILogger, string, int, Exception?> s_connectingToResonite =
            LoggerMessage.Define<string, int>(
                LogLevel.Information,
                new EventId(1, "ConnectingToResonite"),
                "Connecting to Resonite Link at {Host}:{Port}");

        private static readonly Action<ILogger, Exception?> s_fetchingRootTileset =
            LoggerMessage.Define(
                LogLevel.Information,
                new EventId(2, "FetchingRootTileset"),
                "Fetching root tileset from Google Map Tiles API.");

        private static readonly Action<ILogger, Exception?> s_failedToSetInitialFetchProgress =
            LoggerMessage.Define(
                LogLevel.Warning,
                new EventId(3, "FailedToSetInitialFetchProgress"),
                "Failed to set initial fetch progress.");

        private static readonly Action<ILogger, string, Uri, Exception?> s_discoveryNestedLoaded =
            LoggerMessage.Define<string, Uri>(
                LogLevel.Information,
                new EventId(4, "DiscoveryNestedLoaded"),
                "Loaded nested tileset for tile {TileId} from {Uri}.");

        private static readonly Action<ILogger, string, Uri, int, Exception?> s_discoveryTilePrepared =
            LoggerMessage.Define<string, Uri, int>(
                LogLevel.Information,
                new EventId(5, "DiscoveryTilePrepared"),
                "Prepared tile {TileId} from {Uri} with {MeshCount} meshes.");

        private static readonly Action<ILogger, string, Uri, Exception?> s_discoverySkippedWithError =
            LoggerMessage.Define<string, Uri>(
                LogLevel.Warning,
                new EventId(6, "DiscoverySkippedWithError"),
                "Skipped tile {TileId} from {Uri} after fetch/process error.");

        private static readonly Action<ILogger, string, Uri, string, Exception?> s_discoverySkipped =
            LoggerMessage.Define<string, Uri, string>(
                LogLevel.Information,
                new EventId(7, "DiscoverySkipped"),
                "Skipped tile {TileId} from {Uri}: {Reason}");

        private static readonly Action<ILogger, string, Uri, Exception?> s_discoveryFailed =
            LoggerMessage.Define<string, Uri>(
                LogLevel.Warning,
                new EventId(8, "DiscoveryFailed"),
                "Failed to discover tile {TileId} from {Uri}.");

        private static readonly Action<ILogger, string, int, int, Exception?> s_writerTileStreamed =
            LoggerMessage.Define<string, int, int>(
                LogLevel.Information,
                new EventId(9, "WriterTileStreamed"),
                "Streamed tile {TileId}: meshes={MeshCount}, slots={SlotCount}.");

        private static readonly Action<ILogger, string, int, int, Exception?> s_writerTileFailed =
            LoggerMessage.Define<string, int, int>(
                LogLevel.Warning,
                new EventId(10, "WriterTileFailed"),
                "Failed to stream tile {TileId}: streamedMeshes={MeshCount}, slots={SlotCount}.");

        private static readonly Action<ILogger, string, Exception?> s_writerTileRemoved =
            LoggerMessage.Define<string>(
                LogLevel.Information,
                new EventId(11, "WriterTileRemoved"),
                "Removed tile {TileId}.");

        private static readonly Action<ILogger, string, double, Exception?> s_writerTileRemovedWithLifetime =
            LoggerMessage.Define<string, double>(
                LogLevel.Information,
                new EventId(11, "WriterTileRemoved"),
                "Removed tile {TileId}: visibleMs={VisibleMilliseconds}.");

        private static readonly Action<ILogger, string, int, Exception?> s_writerTileRemovalFailed =
            LoggerMessage.Define<string, int>(
                LogLevel.Warning,
                new EventId(12, "WriterTileRemovalFailed"),
                "Failed to fully remove tile {TileId}: remainingSlots={RemainingSlotCount}.");

        private static readonly Action<ILogger, float, string, Exception?> s_writerMetadataUpdated =
            LoggerMessage.Define<float, string>(
                LogLevel.Information,
                new EventId(13, "WriterMetadataUpdated"),
                "Updated session metadata: progress={Progress:P0}, text={ProgressText}.");

        private static readonly Action<ILogger, float, string, Exception?> s_writerMetadataFailed =
            LoggerMessage.Define<float, string>(
                LogLevel.Warning,
                new EventId(14, "WriterMetadataFailed"),
                "Failed to update session metadata: progress={Progress:P0}, text={ProgressText}.");

        private static readonly Action<ILogger, string, string, Exception?> s_rollBackPartialSendFailed =
            LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(15, "RollBackPartialSendFailed"),
                "Failed to roll back partially streamed slot {SlotId} for tile {TileId}.");

        private static readonly Action<ILogger, Exception?> s_disconnectFailedDuringFailure =
            LoggerMessage.Define(
                LogLevel.Warning,
                new EventId(18, "DisconnectFailedDuringFailure"),
                "Failed to disconnect Resonite Link while another failure was already in flight.");

        private static readonly Action<ILogger, int, int, int, int, string, string, Exception?> s_progressSnapshot =
            LoggerMessage.Define<int, int, int, int, string, string>(
                LogLevel.Information,
                new EventId(19, "ProgressSnapshot"),
                "Progress snapshot: candidate={CandidateTiles} processed={ProcessedTiles} streamedMeshes={StreamedMeshes} failed={FailedTiles} state={State} perf={PerfMs}.");

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Initial progress reporting intentionally tolerates transient session errors while continuing orchestration.")]
        public async Task<TileSelectionRunExecutionResult> RunAsync(
            TileRunRequest request,
            InteractiveRunInput? interactiveInput,
            TileSelectionInteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            GoogleTilesAuth auth = await BuildAuthAsync(request).ConfigureAwait(false);
            if (!request.Output.DryRun && request.Output.ManageConnection)
            {
                s_connectingToResonite(_logger, request.Output.Host, request.Output.Port, null);
                await _resoniteSession.ConnectAsync(request.Output.Host, request.Output.Port, cancellationToken).ConfigureAwait(false);
            }

            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var discoveryTasks = new Dictionary<string, DiscoveryTaskEntry>(StringComparer.Ordinal);
            var writerTasks = new List<Task<WriterCompletion>>();
            ExceptionDispatchInfo? pendingFailure = null;
            bool completedSuccessfully = false;
            DateTimeOffset lastProgressReportAt = DateTimeOffset.MinValue;

            DiscoveryFacts? facts = null;
            WriterState? writerState = null;
            var counters = new TileSelectionRunCounters();

            try
            {
                s_fetchingRootTileset(_logger, null);
                if (!request.Output.DryRun)
                {
                    try
                    {
                        await _sessionMetadataPort.SetProgressAsync(
                            request.Output.MeshParentSlotId,
                            0f,
                            "Fetching root tileset...",
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        s_failedToSetInitialFetchProgress(_logger, ex);
                    }
                }

                Tiles.Tileset rootTileset = await _tilesSource.FetchRootTilesetAsync(auth, cancellationToken).ConfigureAwait(false);
                facts = _traversalCore.Initialize(rootTileset, request, interactiveInput);
                writerState = new WriterState(interactiveInput?.RetainedTiles, interactiveInput?.CleanupDebtTiles);
                UpdateInteractiveState(interactiveContext, facts, writerState, counters);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    SelectionState selectionState = writerState.CreateSelectionState();
                    DesiredView desiredView = _traversalCore.ComputeDesiredView(facts, selectionState);
                    ProgressSnapshot progress = BuildProgressSnapshot(facts, writerState, counters);

                    int nestedLoadsInFlight = discoveryTasks.Values.Count(static entry => entry.Work is LoadNestedTilesetWorkItem);
                    int tilePreparesInFlight = discoveryTasks.Values.Count(static entry => entry.Work is PrepareTileWorkItem);
                    int availableNestedTilesetLoads = global::System.Math.Max(0, _maxConcurrentNestedTilesetLoads - nestedLoadsInFlight);
                    int availableTilePrepares = global::System.Math.Max(0, _maxConcurrentTileProcessing - tilePreparesInFlight);
                    foreach (DiscoveryWorkItem work in _traversalCore.PlanDiscovery(
                                 facts,
                                 selectionState,
                                 availableNestedTilesetLoads,
                                 availableTilePrepares))
                    {
                        string stableId = work.Tile.StableId!;
                        if (discoveryTasks.ContainsKey(stableId))
                        {
                            continue;
                        }

                        MarkDiscoveryInFlight(facts, work);
                        discoveryTasks.Add(stableId, new DiscoveryTaskEntry(
                            work,
                            ExecuteDiscoveryAsync(work, request, auth, workerCts.Token)));
                    }

                    DateTimeOffset planningTime = DateTimeOffset.UtcNow;
                    WriterPlan writerPlan = _reconcilerCore.ReduceWriterPlan(
                        facts,
                        writerState,
                        selectionState,
                        desiredView,
                        progress,
                        request.Output.DryRun,
                        _maxConcurrentWriterSends,
                        planningTime);

                    if (writerPlan.ControlCommand is not null)
                    {
                        MarkWriterInFlight(writerState, writerPlan.ControlCommand, planningTime);
                        writerTasks.Add(ExecuteWriterCommandAsync(writerPlan.ControlCommand, request, interactiveContext, workerCts.Token));
                    }

                    foreach (SendTileWriterCommand sendWriterCommand in writerPlan.SendCommands)
                    {
                        MarkWriterInFlight(writerState, sendWriterCommand, planningTime);
                        writerTasks.Add(ExecuteWriterCommandAsync(sendWriterCommand, request, interactiveContext, workerCts.Token));
                    }

                    selectionState = writerState.CreateSelectionState();
                    desiredView = _traversalCore.ComputeDesiredView(facts, selectionState);
                    progress = BuildProgressSnapshot(facts, writerState, counters);
                    UpdateInteractiveState(interactiveContext, facts, writerState, counters);
                    MaybeLogProgressSnapshot(progress, writerState, discoveryTasks.Count, writerTasks.Count, ref lastProgressReportAt);

                    bool writerBusy = writerTasks.Count != 0;
                    if (discoveryTasks.Count == 0 && !writerBusy && _reconcilerCore.IsReconciled(
                        facts,
                        writerState,
                        desiredView,
                        progress,
                        request.Output.DryRun))
                    {
                        completedSuccessfully = true;
                        return BuildRunExecutionResult(facts, writerState, counters);
                    }

                    if (discoveryTasks.Count == 0 && writerTasks.Count == 0)
                    {
                        throw new InvalidOperationException("Traversal stalled without pending discovery or writer work.");
                    }

                    if (writerTasks.Count == 0)
                    {
                        Task completedTask = await Task.WhenAny(discoveryTasks.Values.Select(static entry => entry.Task)).ConfigureAwait(false);
                        if (!completedTask.IsCompleted)
                        {
                            throw new InvalidOperationException("Completed task was not completed.");
                        }
                    }
                    else
                    {
                        Task completedTask = await Task.WhenAny(discoveryTasks.Values.Select(static entry => entry.Task).Cast<Task>().Concat(writerTasks)).ConfigureAwait(false);
                        if (!completedTask.IsCompleted)
                        {
                            throw new InvalidOperationException("Completed task was not completed.");
                        }
                    }

                    await ApplyCompletedWorkAsync(
                        facts,
                        writerState,
                        discoveryTasks,
                        interactiveContext,
                        writerTasks,
                        counters).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (facts is not null && writerState is not null)
            {
                pendingFailure = ExceptionDispatchInfo.Capture(new OperationCanceledException(cancellationToken));
                await workerCts.CancelAsync().ConfigureAwait(false);
                await ApplyCompletedWorkAsync(
                    facts,
                    writerState,
                    discoveryTasks,
                    interactiveContext,
                    writerTasks,
                    counters).ConfigureAwait(false);
                await DrainOutstandingWorkAsync(
                    facts,
                    writerState,
                    discoveryTasks,
                    interactiveContext,
                    writerTasks,
                    counters).ConfigureAwait(false);
                UpdateInteractiveState(interactiveContext, facts, writerState, counters);
                throw;
            }
            catch (Exception ex)
            {
                pendingFailure = ExceptionDispatchInfo.Capture(ex);
                throw;
            }
            finally
            {
                await workerCts.CancelAsync().ConfigureAwait(false);
                await ObserveOutstandingTasksAsync(discoveryTasks.Values, writerTasks).ConfigureAwait(false);

                if (!request.Output.DryRun && request.Output.ManageConnection)
                {
                    if (completedSuccessfully && pendingFailure is null)
                    {
                        await _resoniteSession.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            await _resoniteSession.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            s_disconnectFailedDuringFailure(_logger, ex);
                        }
                    }
                }
            }
        }

        private void MaybeLogProgressSnapshot(
            ProgressSnapshot progress,
            WriterState writerState,
            int discoveryInFlight,
            int writerInFlight,
            ref DateTimeOffset lastReportAt)
        {
            if (_performanceSummary is null)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (lastReportAt != DateTimeOffset.MinValue && now - lastReportAt < TimeSpan.FromSeconds(10))
            {
                return;
            }

            lastReportAt = now;
            string state = $"disc={discoveryInFlight} writer={writerInFlight} sendInFlight={writerState.InFlightSendStableIds.Count} " +
                           $"removeInFlight={(writerState.InFlightRemoveStableId is null ? 0 : 1)} metadataInFlight={(writerState.MetadataInFlight ? 1 : 0)}";
            string perf = $"fetch={_performanceSummary.FetchMilliseconds} extract={_performanceSummary.ExtractMilliseconds} " +
                          $"placement={_performanceSummary.PlacementMilliseconds} send={_performanceSummary.SendMilliseconds} " +
                          $"remove={_performanceSummary.RemoveMilliseconds} " +
                          $"metadataSync={_performanceSummary.MetadataSyncMilliseconds}/{_performanceSummary.MetadataSyncCount} max={_performanceSummary.MetadataSyncMaxMilliseconds} " +
                          $"metadataLicense={_performanceSummary.MetadataLicenseMilliseconds}/{_performanceSummary.MetadataLicenseCount} max={_performanceSummary.MetadataLicenseMaxMilliseconds} " +
                          $"metadataProgress={_performanceSummary.MetadataProgressMilliseconds}/{_performanceSummary.MetadataProgressCount} max={_performanceSummary.MetadataProgressMaxMilliseconds}";
            s_progressSnapshot(
                _logger,
                progress.CandidateTiles,
                progress.ProcessedTiles,
                progress.StreamedMeshes,
                progress.FailedTiles,
                state,
                perf,
                null);
        }

        private async Task ApplyCompletedWorkAsync(
            DiscoveryFacts facts,
            WriterState writerState,
            Dictionary<string, DiscoveryTaskEntry> discoveryTasks,
            TileSelectionInteractiveExecutionContext? interactiveContext,
            List<Task<WriterCompletion>> writerTasks,
            TileSelectionRunCounters counters)
        {
            foreach ((string stableId, DiscoveryTaskEntry entry) in discoveryTasks
                         .Where(static pair => pair.Value.Task.IsCompleted)
                         .ToArray())
            {
                _ = discoveryTasks.Remove(stableId);
                try
                {
                    DiscoveryCompletion completion = await entry.Task.ConfigureAwait(false);
                    LogDiscoveryCompletion(completion);
                    long nextPreparedOrder = counters.NextPreparedOrder;
                    int processedTiles = counters.ProcessedTiles;
                    int failedTiles = counters.FailedTiles;
                    _traversalCore.ApplyDiscoveryCompletion(
                        facts,
                        completion,
                        ref nextPreparedOrder,
                        ref processedTiles,
                        ref failedTiles);
                    counters.NextPreparedOrder = nextPreparedOrder;
                    counters.ProcessedTiles = processedTiles;
                    counters.FailedTiles = failedTiles;
                }
                catch (OperationCanceledException)
                {
                    RestoreDiscoveryStatus(facts, stableId);
                }
            }

            foreach (Task<WriterCompletion> writerTask in writerTasks
                         .Where(static task => task.IsCompleted)
                         .ToArray())
            {
                _ = writerTasks.Remove(writerTask);

                try
                {
                    WriterCompletion completion = await writerTask.ConfigureAwait(false);
                    LogWriterCompletion(completion, writerState);
                    if (completion is SendTileCompleted sent)
                    {
                        foreach (string slotId in sent.SlotIds)
                        {
                            interactiveContext?.TrackNewSlotId(slotId);
                        }
                    }

                    int processedTiles = counters.ProcessedTiles;
                    int streamedMeshes = counters.StreamedMeshes;
                    int failedTiles = counters.FailedTiles;
                    ResoniteReconcilerCore.ApplyWriterCompletion(
                        facts,
                        writerState,
                        completion,
                        dryRun: facts.Request.Output.DryRun,
                        ref processedTiles,
                        ref streamedMeshes,
                        ref failedTiles);
                    counters.ProcessedTiles = processedTiles;
                    counters.StreamedMeshes = streamedMeshes;
                    counters.FailedTiles = failedTiles;
                }
                catch (OperationCanceledException)
                {
                    ClearWriterInFlight(writerState);
                }
            }
        }

        private async Task DrainOutstandingWorkAsync(
            DiscoveryFacts facts,
            WriterState writerState,
            Dictionary<string, DiscoveryTaskEntry> discoveryTasks,
            TileSelectionInteractiveExecutionContext? interactiveContext,
            List<Task<WriterCompletion>> writerTasks,
            TileSelectionRunCounters counters)
        {
            while (discoveryTasks.Count > 0 || writerTasks.Count != 0)
            {
                if (writerTasks.Count == 0)
                {
                    _ = await Task.WhenAny(discoveryTasks.Values.Select(static entry => entry.Task)).ConfigureAwait(false);
                }
                else
                {
                    _ = await Task.WhenAny(discoveryTasks.Values.Select(static entry => entry.Task).Cast<Task>().Concat(writerTasks)).ConfigureAwait(false);
                }

                await ApplyCompletedWorkAsync(
                    facts,
                    writerState,
                    discoveryTasks,
                    interactiveContext,
                    writerTasks,
                    counters).ConfigureAwait(false);
            }
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Outstanding writer/discovery failures are intentionally ignored to complete cleanup and surface cancellation.")]
        private static async Task ObserveOutstandingTasksAsync(
            IEnumerable<DiscoveryTaskEntry> discoveryTasks,
            IEnumerable<Task<WriterCompletion>> writerTasks)
        {
            IEnumerable<Task> outstanding = discoveryTasks.Select(static entry => entry.Task).Cast<Task>().Concat(writerTasks);

            try
            {
                await Task.WhenAll(outstanding).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }

        private void LogDiscoveryCompletion(DiscoveryCompletion completion)
        {
            switch (completion)
            {
                case NestedTilesetDiscovered nested:
                    s_discoveryNestedLoaded(_logger, nested.Tile.TileId, nested.Tile.ContentUri, null);
                    break;

                case TilePrepared prepared:
                    s_discoveryTilePrepared(_logger, prepared.Tile.TileId, prepared.Tile.ContentUri, prepared.Content.Meshes.Count, null);
                    break;

                case DiscoverySkipped skipped when skipped.Error is not null:
                    s_discoverySkippedWithError(_logger, skipped.Tile.TileId, skipped.Tile.ContentUri, skipped.Error);
                    break;

                case DiscoverySkipped skipped:
                    s_discoverySkipped(_logger, skipped.Tile.TileId, skipped.Tile.ContentUri, skipped.Reason ?? "no reason", null);
                    break;

                case DiscoveryFailed failed:
                    s_discoveryFailed(_logger, failed.Tile.TileId, failed.Tile.ContentUri, failed.Error);
                    break;
            }
        }

        private void LogWriterCompletion(WriterCompletion completion, WriterState writerState)
        {
            switch (completion)
            {
                case SendTileCompleted sent when sent.Succeeded:
                    s_writerTileStreamed(_logger, sent.Content.Tile.TileId, sent.StreamedMeshCount, sent.SlotIds.Count, null);
                    break;

                case SendTileCompleted sent:
                    s_writerTileFailed(_logger, sent.Content.Tile.TileId, sent.StreamedMeshCount, sent.SlotIds.Count, sent.Error);
                    break;

                case RemoveTileCompleted removed when removed.Succeeded:
                    if (_performanceSummary is not null &&
                        writerState.VisibleSinceByStableId.TryGetValue(removed.StableId, out DateTimeOffset visibleSince) &&
                        visibleSince != DateTimeOffset.MinValue)
                    {
                        s_writerTileRemovedWithLifetime(_logger, removed.TileId, (DateTimeOffset.UtcNow - visibleSince).TotalMilliseconds, null);
                    }
                    else
                    {
                        s_writerTileRemoved(_logger, removed.TileId, null);
                    }
                    break;

                case RemoveTileCompleted removed:
                    s_writerTileRemovalFailed(_logger, removed.TileId, removed.RemainingSlotIds.Count, removed.Error);
                    break;

                case CleanupTileCompleted cleanup when cleanup.Succeeded:
                    s_writerTileRemoved(_logger, cleanup.TileId, null);
                    break;

                case CleanupTileCompleted cleanup:
                    s_writerTileRemovalFailed(_logger, cleanup.TileId, cleanup.RemainingSlotIds.Count, cleanup.Error);
                    break;

                case SyncSessionMetadataCompleted metadata when metadata.Succeeded:
                    s_writerMetadataUpdated(_logger, metadata.ProgressValue, metadata.ProgressText, null);
                    break;

                case SyncSessionMetadataCompleted metadata:
                    s_writerMetadataFailed(_logger, metadata.ProgressValue, metadata.ProgressText, metadata.Error);
                    break;
            }
        }

        private static void MarkDiscoveryInFlight(DiscoveryFacts facts, DiscoveryWorkItem work)
        {
            if (!facts.Branches.TryGetValue(work.Tile.StableId!, out TileBranchFact? fact))
            {
                return;
            }

            switch (work)
            {
                case LoadNestedTilesetWorkItem:
                    fact.NestedStatus = ContentDiscoveryStatus.InFlight;
                    break;
                case PrepareTileWorkItem:
                    fact.PrepareStatus = ContentDiscoveryStatus.InFlight;
                    break;
            }
        }

        private static void RestoreDiscoveryStatus(DiscoveryFacts facts, string stableId)
        {
            if (!facts.Branches.TryGetValue(stableId, out TileBranchFact? fact))
            {
                return;
            }

            if (fact.Tile.ContentKind == TileContentKind.Json &&
                fact.NestedStatus == ContentDiscoveryStatus.InFlight)
            {
                fact.NestedStatus = ContentDiscoveryStatus.Unrequested;
            }
            else if (fact.Tile.ContentKind == TileContentKind.Glb &&
                     fact.PrepareStatus == ContentDiscoveryStatus.InFlight)
            {
                fact.PrepareStatus = ContentDiscoveryStatus.Unrequested;
            }
        }

        private static void MarkWriterInFlight(WriterState writerState, WriterCommand command, DateTimeOffset now)
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

        private static void ClearWriterInFlight(WriterState writerState)
        {
            writerState.InFlightSendStableIds.Clear();
            writerState.InFlightRemoveStableId = null;
            writerState.MetadataInFlight = false;
        }

        private ProgressSnapshot BuildProgressSnapshot(
            DiscoveryFacts facts,
            WriterState writerState,
            TileSelectionRunCounters counters)
        {
            SelectionState selectionState = writerState.CreateSelectionState();
            return new ProgressSnapshot(
                _traversalCore.CountCandidateTiles(facts, selectionState),
                counters.ProcessedTiles,
                counters.StreamedMeshes,
                counters.FailedTiles);
        }

        private TileSelectionRunExecutionResult BuildRunExecutionResult(
            DiscoveryFacts facts,
            WriterState writerState,
            TileSelectionRunCounters counters)
        {
            SelectionState selectionState = writerState.CreateSelectionState();
            DesiredView desiredView = _traversalCore.ComputeDesiredView(facts, selectionState);
            var summary = new RunSummary(
                _traversalCore.CountCandidateTiles(facts, selectionState),
                counters.ProcessedTiles,
                counters.StreamedMeshes,
                counters.FailedTiles);
            IReadOnlySet<string> selectedTileStableIds = desiredView.SelectedStableIds is HashSet<string> selectedHashSet
                ? selectedHashSet
                : desiredView.SelectedStableIds.ToHashSet(StringComparer.Ordinal);
            Dictionary<string, RetainedTileState> visibleTiles = _traversalCore.BuildVisibleTiles(writerState)
                .Where(pair => facts.Branches.ContainsKey(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            Dictionary<string, RetainedTileState> cleanupDebtTiles = new(writerState.CleanupDebtTiles, StringComparer.Ordinal);

            return new TileSelectionRunExecutionResult(
                summary,
                visibleTiles,
                cleanupDebtTiles,
                selectedTileStableIds,
                _traversalCore.BuildCheckpoint(facts));
        }

        private void UpdateInteractiveState(
            TileSelectionInteractiveExecutionContext? interactiveContext,
            DiscoveryFacts facts,
            WriterState writerState,
            TileSelectionRunCounters counters)
        {
            if (interactiveContext is null)
            {
                return;
            }

            TileSelectionRunExecutionResult state = BuildRunExecutionResult(facts, writerState, counters);
            interactiveContext.UpdateState(state);
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Tile discovery intentionally records unsupported processing failures as DiscoveryFailed.")]
        private async Task<DiscoveryCompletion> ExecuteDiscoveryAsync(
            DiscoveryWorkItem work,
            TileRunRequest request,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            try
            {
                ContentProcessResult content = await _contentProcessor.ProcessAsync(work.Tile, auth, cancellationToken).ConfigureAwait(false);
                return content switch
                {
                    NestedTilesetContentProcessResult nested => new NestedTilesetDiscovered(work.Tile, nested.Tileset),
                    RenderableContentProcessResult renderable => new TilePrepared(
                        work.Tile,
                        new PreparedTileContent(
                            work.Tile,
                            MeasurePlacement(() => _meshPlacementService.Place(
                                work.Tile,
                                renderable.Meshes,
                                request.PlacementReference,
                                request.Output.MeshParentSlotId)),
                            renderable.AssetCopyright)),
                    SkippedContentProcessResult skipped => new DiscoverySkipped(work.Tile, skipped.Reason),
                    _ => throw new InvalidOperationException("Unsupported content process result: " + content.GetType().Name)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return new DiscoverySkipped(work.Tile, Error: ex);
            }
            catch (Exception ex)
            {
                return new DiscoveryFailed(work.Tile, ex);
            }
        }

        private async Task<WriterCompletion> ExecuteWriterCommandAsync(
            WriterCommand command,
            TileRunRequest request,
            TileSelectionInteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            return command switch
            {
                SendTileWriterCommand send => await ExecuteSendAsync(send, request, cancellationToken).ConfigureAwait(false),
                RemoveTileWriterCommand remove => await ExecuteRemoveAsync(remove, interactiveContext, cancellationToken).ConfigureAwait(false),
                CleanupTileWriterCommand cleanup => await ExecuteCleanupAsync(cleanup, interactiveContext, cancellationToken).ConfigureAwait(false),
                DelayWriterCommand delay => await ExecuteDelayAsync(delay, cancellationToken).ConfigureAwait(false),
                SyncSessionMetadataWriterCommand metadata => await ExecuteSyncMetadataAsync(request, metadata, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported writer command type: " + command.GetType().Name)
            };
        }

        private static async Task<WriterCompletion> ExecuteDelayAsync(
            DelayWriterCommand command,
            CancellationToken cancellationToken)
        {
            if (command.Delay > TimeSpan.Zero)
            {
                await Task.Delay(command.Delay, cancellationToken).ConfigureAwait(false);
            }

            return new DelayCompleted(command.Delay);
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Send failures are intentionally converted to completion states to continue orchestration.")]
        private async Task<WriterCompletion> ExecuteSendAsync(
            SendTileWriterCommand command,
            TileRunRequest request,
            CancellationToken cancellationToken)
        {
            RunPerformanceSummary? performanceSummary = _performanceSummary;
            long startedAt = performanceSummary is null ? 0L : Stopwatch.GetTimestamp();
            Task<TileSelectionSendMeshResult>[] sendTasks = command.Content.Meshes.Select(SendPayloadAsync).ToArray();
            try
            {
                TileSelectionSendMeshResult[] results = await Task.WhenAll(sendTasks).ConfigureAwait(false);
                var streamedSlotIds = results
                    .Select(static result => result.SlotId)
                    .Where(static slotId => !string.IsNullOrWhiteSpace(slotId))
                    .Cast<string>()
                    .ToList();
                int streamedMeshCount = results.Count(static result => result.Error is null);
                Exception? firstError = results.FirstOrDefault(static result => result.Error is not null)?.Error;

                if (firstError is null)
                {
                    return new SendTileCompleted(command.Content, true, streamedMeshCount, streamedSlotIds);
                }

                if (streamedSlotIds.Count > 0)
                {
                    IReadOnlyList<string> remainingSlotIds = await TryRollbackPartialSendAsync(
                        command.Content.Tile.TileId,
                        streamedSlotIds,
                        cancellationToken).ConfigureAwait(false);
                    if (remainingSlotIds.Count == 0)
                    {
                        streamedSlotIds.Clear();
                        streamedMeshCount = 0;
                    }
                    else
                    {
                        streamedSlotIds = [.. remainingSlotIds];
                        streamedMeshCount = streamedSlotIds.Count;
                    }
                }

                return new SendTileCompleted(command.Content, false, streamedMeshCount, streamedSlotIds, firstError);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                List<string> streamedSlotIds = sendTasks
                    .Where(static task => task.Status == TaskStatus.RanToCompletion)
                    .Select(static task => task.Result.SlotId)
                    .Where(static slotId => !string.IsNullOrWhiteSpace(slotId))
                    .Cast<string>()
                    .ToList();
                if (streamedSlotIds.Count > 0)
                {
                    _ = await TryRollbackPartialSendAsync(
                        command.Content.Tile.TileId,
                        streamedSlotIds,
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
            finally
            {
                if (performanceSummary is not null)
                {
                    performanceSummary.AddSend(Stopwatch.GetElapsedTime(startedAt));
                }
            }

            async Task<TileSelectionSendMeshResult> SendPayloadAsync(PlacedMeshPayload payload)
            {
                try
                {
                    string? slotId = request.Output.DryRun
                        ? null
                        : await _resoniteSession.StreamPlacedMeshAsync(payload, cancellationToken).ConfigureAwait(false);
                    return new TileSelectionSendMeshResult(slotId, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new TileSelectionSendMeshResult(null, ex);
                }
            }
        }

        private IReadOnlyList<PlacedMeshPayload> MeasurePlacement(Func<IReadOnlyList<PlacedMeshPayload>> action)
        {
            RunPerformanceSummary? performanceSummary = _performanceSummary;
            if (performanceSummary is null)
            {
                return action();
            }

            long startedAt = Stopwatch.GetTimestamp();
            IReadOnlyList<PlacedMeshPayload> result = action();
            performanceSummary.AddPlacement(Stopwatch.GetElapsedTime(startedAt));
            return result;
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Slot rollback is best-effort; failures are captured as failed state.")]
        private async Task<IReadOnlyList<string>> TryRollbackPartialSendAsync(
            string tileId,
            IReadOnlyList<string> streamedSlotIds,
            CancellationToken cancellationToken)
        {
            var remainingSlotIds = new List<string>();

            foreach (string slotId in streamedSlotIds)
            {
                try
                {
                    await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception rollbackEx)
                {
                    remainingSlotIds.Add(slotId);
                    s_rollBackPartialSendFailed(_logger, slotId, tileId, rollbackEx);
                }
            }

            return remainingSlotIds;
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Tile removal captures slot failures to return retry-aware completion state.")]
        private async Task<WriterCompletion> ExecuteRemoveAsync(
            RemoveTileWriterCommand command,
            TileSelectionInteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            return await ExecuteSlotRemovalAsync(
                command.StableId,
                command.TileId,
                command.SlotIds,
                interactiveContext,
                isCleanupDebt: false,
                cancellationToken).ConfigureAwait(false);
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Cleanup debt removal captures slot failures to return retry-aware completion state.")]
        private async Task<WriterCompletion> ExecuteCleanupAsync(
            CleanupTileWriterCommand command,
            TileSelectionInteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            return await ExecuteSlotRemovalAsync(
                command.StableId,
                command.TileId,
                command.SlotIds,
                interactiveContext,
                isCleanupDebt: true,
                cancellationToken).ConfigureAwait(false);
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Slot removal captures failures to return retry-aware completion state.")]
        private async Task<WriterCompletion> ExecuteSlotRemovalAsync(
            string stableId,
            string tileId,
            IReadOnlyList<string> slotIds,
            TileSelectionInteractiveExecutionContext? interactiveContext,
            bool isCleanupDebt,
            CancellationToken cancellationToken)
        {
            RunPerformanceSummary? performanceSummary = _performanceSummary;
            long startedAt = performanceSummary is null ? 0L : Stopwatch.GetTimestamp();
            try
            {
                int failedSlotCount = 0;
                Exception? firstError = null;
                var remainingSlotIds = new List<string>();

                foreach (string slotId in slotIds)
                {
                    try
                    {
                        await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                        interactiveContext?.ForgetNewSlotId(slotId);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failedSlotCount++;
                        firstError ??= ex;
                        remainingSlotIds.Add(slotId);
                    }
                }

                return isCleanupDebt
                    ? new CleanupTileCompleted(stableId, tileId, failedSlotCount == 0, failedSlotCount, remainingSlotIds, firstError)
                    : new RemoveTileCompleted(stableId, tileId, failedSlotCount == 0, failedSlotCount, remainingSlotIds, firstError);
            }
            finally
            {
                if (performanceSummary is not null)
                {
                    performanceSummary.AddRemove(Stopwatch.GetElapsedTime(startedAt));
                }
            }
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Metadata sync failures are intentionally surfaced in command completion result.")]
        private async Task<WriterCompletion> ExecuteSyncMetadataAsync(
            TileRunRequest request,
            SyncSessionMetadataWriterCommand command,
            CancellationToken cancellationToken)
        {
            RunPerformanceSummary? performanceSummary = _performanceSummary;
            long syncStartedAt = performanceSummary is null ? 0L : Stopwatch.GetTimestamp();
            try
            {
                if (command.UpdateLicense)
                {
                    long licenseStartedAt = performanceSummary is null ? 0L : Stopwatch.GetTimestamp();
                    await _sessionMetadataPort.SetSessionLicenseCreditAsync(command.LicenseCredit, cancellationToken).ConfigureAwait(false);
                    if (performanceSummary is not null)
                    {
                        performanceSummary.AddMetadataLicense(Stopwatch.GetElapsedTime(licenseStartedAt));
                    }
                }

                long progressStartedAt = performanceSummary is null ? 0L : Stopwatch.GetTimestamp();
                await _sessionMetadataPort.SetProgressValueAsync(
                    request.Output.MeshParentSlotId,
                    command.ProgressValue,
                    cancellationToken).ConfigureAwait(false);
                if (command.UpdateProgressText)
                {
                    await _sessionMetadataPort.SetProgressTextAsync(
                        request.Output.MeshParentSlotId,
                        command.ProgressText,
                        cancellationToken).ConfigureAwait(false);
                }

                if (performanceSummary is not null)
                {
                    performanceSummary.AddMetadataProgress(Stopwatch.GetElapsedTime(progressStartedAt));
                }

                return new SyncSessionMetadataCompleted(command.LicenseCredit, command.ProgressValue, command.ProgressText, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new SyncSessionMetadataCompleted(command.LicenseCredit, command.ProgressValue, command.ProgressText, false, ex);
            }
            finally
            {
                if (performanceSummary is not null)
                {
                    performanceSummary.AddMetadataSync(Stopwatch.GetElapsedTime(syncStartedAt));
                }
            }
        }

        private static Task<GoogleTilesAuth> BuildAuthAsync(TileRunRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return Task.FromResult(new GoogleTilesAuth(request.ApiKey, null));
            }

            throw new InvalidOperationException("GOOGLE_MAPS_API_KEY is required for Google Map Tiles API requests.");
        }
    }
}
