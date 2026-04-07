using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

#pragma warning disable CA1822

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TileSelectionService(
        ITilesSource tilesSource,
        TraversalCore traversalCore,
        ResoniteReconcilerCore reconcilerCore,
        IContentProcessor contentProcessor,
        IMeshPlacementService meshPlacementService,
        ISelectedTileProjector selectedTileProjector,
        ILogger logger,
        int maxConcurrentTileProcessing = 1,
        int maxConcurrentWriterSends = 1,
        RunPerformanceSummary? performanceSummary = null) : ITileSelectionService
    {
        private readonly ITilesSource _tilesSource = tilesSource;
        private readonly TraversalCore _traversalCore = traversalCore;
        private readonly ResoniteReconcilerCore _reconcilerCore = reconcilerCore;
        private readonly IContentProcessor _contentProcessor = contentProcessor;
        private readonly IMeshPlacementService _meshPlacementService = meshPlacementService;
        private readonly ISelectedTileProjector _selectedTileProjector = selectedTileProjector;
        private readonly ILogger _logger = logger;
        private readonly int _maxConcurrentTileProcessing = maxConcurrentTileProcessing > 0
            ? maxConcurrentTileProcessing
            : throw new ArgumentOutOfRangeException(nameof(maxConcurrentTileProcessing), "Tile content worker count must be positive.");
        private readonly int _maxConcurrentWriterSends = maxConcurrentWriterSends > 0
            ? maxConcurrentWriterSends
            : throw new ArgumentOutOfRangeException(nameof(maxConcurrentWriterSends), "Writer send concurrency must be positive.");
        private readonly int _maxConcurrentNestedTilesetLoads = System.Math.Max(
            2,
            maxConcurrentTileProcessing);
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

        private static readonly Action<ILogger, string, Exception?> s_rollbackStreamedSlotFailed =
            LoggerMessage.Define<string>(
                LogLevel.Warning,
                new EventId(16, "RollbackStreamedSlotFailed"),
                "Failed to rollback newly streamed slot {SlotId}.");

        private static readonly Action<ILogger, string, string, Exception?> s_removeRetainedSlotFailed =
            LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(17, "RemoveRetainedSlotFailed"),
                "Failed to remove retained slot {SlotId} for tile {TileId}.");

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

        public async Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            RunExecutionResult result = await RunCoreAsync(request, interactiveInput: null, interactiveContext: null, cancellationToken).ConfigureAwait(false);
            return result.Summary;
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Interactive run intentionally degrades to rollback path and rethrows for any unexpected exception.")]
        public async Task<InteractiveTileRunResult> RunInteractiveAsync(
            TileRunRequest request,
            InteractiveRunInput interactive,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(interactive);

            var interactiveContext = new InteractiveExecutionContext(interactive.RetainedTiles, interactive.RemoveOutOfRangeTiles);

            try
            {
                RunExecutionResult execution = await RunCoreAsync(request, interactive, interactiveContext, cancellationToken).ConfigureAwait(false);
                IReadOnlyDictionary<string, RetainedTileState> failedRetainedTiles = await CommitInteractiveChangesAsync(
                    interactiveContext,
                    execution.SelectedTileStableIds,
                    cancellationToken).ConfigureAwait(false);

                IReadOnlyDictionary<string, RetainedTileState> nextRetainedTiles = BuildNextRetainedTiles(
                    interactive.RetainedTiles,
                    execution.VisibleTiles,
                    execution.SelectedTileStableIds,
                    interactive.RemoveOutOfRangeTiles,
                    failedRetainedTiles);

                await ApplyFinalLicenseCreditAsync(request, nextRetainedTiles, cancellationToken).ConfigureAwait(false);
                return new InteractiveTileRunResult(
                    execution.Summary,
                    nextRetainedTiles,
                    execution.SelectedTileStableIds,
                    execution.Checkpoint);
            }
            catch (OperationCanceledException)
            {
                return await FinalizeCanceledInteractiveRunAsync(
                    request,
                    interactive,
                    interactiveContext,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollbackInteractiveChangesAsync(request, interactiveContext).ConfigureAwait(false);
                throw;
            }
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Initial progress reporting intentionally tolerates transient session errors while continuing orchestration.")]
        private async Task<RunExecutionResult> RunCoreAsync(
            TileRunRequest request,
            InteractiveRunInput? interactiveInput,
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            GoogleTilesAuth auth = await BuildAuthAsync(request).ConfigureAwait(false);
            if (!request.Output.DryRun && request.Output.ManageConnection)
            {
                s_connectingToResonite(_logger, request.Output.Host, request.Output.Port, null);
                await _selectedTileProjector.ConnectAsync(request.Output.Host, request.Output.Port, cancellationToken).ConfigureAwait(false);
            }

            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var discoveryTasks = new Dictionary<string, DiscoveryTaskEntry>(StringComparer.Ordinal);
            var writerTasks = new List<Task<WriterCompletion>>();
            ExceptionDispatchInfo? pendingFailure = null;
            bool completedSuccessfully = false;
            DateTimeOffset lastProgressReportAt = DateTimeOffset.MinValue;

            DiscoveryFacts? facts = null;
            WriterState? writerState = null;
            var counters = new RunCounters();

            try
            {
                s_fetchingRootTileset(_logger, null);
                if (!request.Output.DryRun)
                {
                    try
                    {
                        await _selectedTileProjector.SetProgressAsync(
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
                writerState = new WriterState(interactiveInput?.RetainedTiles);
                UpdateInteractiveState(interactiveContext, facts, writerState, counters);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    SelectionState selectionState = writerState.CreateSelectionState();
                    DesiredView desiredView = _traversalCore.ComputeDesiredView(facts, selectionState);
                    ProgressSnapshot progress = BuildProgressSnapshot(facts, writerState, counters);

                    int nestedLoadsInFlight = discoveryTasks.Values.Count(static entry => entry.Work is LoadNestedTilesetWorkItem);
                    int tilePreparesInFlight = discoveryTasks.Values.Count(static entry => entry.Work is PrepareTileWorkItem);
                    int availableNestedTilesetLoads = System.Math.Max(0, _maxConcurrentNestedTilesetLoads - nestedLoadsInFlight);
                    int availableTilePrepares = System.Math.Max(0, _maxConcurrentTileProcessing - tilePreparesInFlight);
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

                    WriterPlan writerPlan = _reconcilerCore.ReduceWriterPlan(
                        facts,
                        writerState,
                        selectionState,
                        desiredView,
                        progress,
                        request.Output.DryRun,
                        _maxConcurrentWriterSends);

                    if (writerPlan.ControlCommand is not null)
                    {
                        MarkWriterInFlight(writerState, writerPlan.ControlCommand);
                        writerTasks.Add(ExecuteWriterCommandAsync(writerPlan.ControlCommand, request, interactiveContext, workerCts.Token));
                    }

                    foreach (SendTileWriterCommand sendWriterCommand in writerPlan.SendCommands)
                    {
                        MarkWriterInFlight(writerState, sendWriterCommand);
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
                        await _selectedTileProjector.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            await _selectedTileProjector.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
                          $"remove={_performanceSummary.RemoveMilliseconds}";
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
            InteractiveExecutionContext? interactiveContext,
            List<Task<WriterCompletion>> writerTasks,
            RunCounters counters)
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
            InteractiveExecutionContext? interactiveContext,
            List<Task<WriterCompletion>> writerTasks,
            RunCounters counters)
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
        private async Task ObserveOutstandingTasksAsync(
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
                    s_discoveryNestedLoaded(
                        _logger,
                        nested.Tile.TileId,
                        nested.Tile.ContentUri,
                        null);
                    break;

                case TilePrepared prepared:
                    s_discoveryTilePrepared(
                        _logger,
                        prepared.Tile.TileId,
                        prepared.Tile.ContentUri,
                        prepared.Content.Meshes.Count,
                        null);
                    break;

                case DiscoverySkipped skipped when skipped.Error is not null:
                    s_discoverySkippedWithError(
                        _logger,
                        skipped.Tile.TileId,
                        skipped.Tile.ContentUri,
                        skipped.Error);
                    break;

                case DiscoverySkipped skipped:
                    s_discoverySkipped(
                        _logger,
                        skipped.Tile.TileId,
                        skipped.Tile.ContentUri,
                        skipped.Reason ?? "no reason",
                        null);
                    break;

                case DiscoveryFailed failed:
                    s_discoveryFailed(
                        _logger,
                        failed.Tile.TileId,
                        failed.Tile.ContentUri,
                        failed.Error);
                    break;
            }
        }

        private void LogWriterCompletion(WriterCompletion completion, WriterState writerState)
        {
            switch (completion)
            {
                case SendTileCompleted sent when sent.Succeeded:
                    s_writerTileStreamed(
                        _logger,
                        sent.Content.Tile.TileId,
                        sent.StreamedMeshCount,
                        sent.SlotIds.Count,
                        null);
                    break;

                case SendTileCompleted sent:
                    s_writerTileFailed(
                        _logger,
                        sent.Content.Tile.TileId,
                        sent.StreamedMeshCount,
                        sent.SlotIds.Count,
                        sent.Error);
                    break;

                case RemoveTileCompleted removed when removed.Succeeded:
                    if (_performanceSummary is not null &&
                        writerState.VisibleSinceByStableId.TryGetValue(removed.StableId, out DateTimeOffset visibleSince) &&
                        visibleSince != DateTimeOffset.MinValue)
                    {
                        s_writerTileRemovedWithLifetime(
                            _logger,
                            removed.TileId,
                            (DateTimeOffset.UtcNow - visibleSince).TotalMilliseconds,
                            null);
                    }
                    else
                    {
                        s_writerTileRemoved(
                            _logger,
                            removed.TileId,
                            null);
                    }
                    break;

                case RemoveTileCompleted removed:
                    s_writerTileRemovalFailed(
                        _logger,
                        removed.TileId,
                        removed.RemainingSlotIds.Count,
                        removed.Error);
                    break;

                case SyncSessionMetadataCompleted metadata when metadata.Succeeded:
                    s_writerMetadataUpdated(
                        _logger,
                        metadata.ProgressValue,
                        metadata.ProgressText,
                        null);
                    break;

                case SyncSessionMetadataCompleted metadata:
                    s_writerMetadataFailed(
                        _logger,
                        metadata.ProgressValue,
                        metadata.ProgressText,
                        metadata.Error);
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

        private static void MarkWriterInFlight(WriterState writerState, WriterCommand command)
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

        private static void ClearWriterInFlight(WriterState writerState)
        {
            writerState.InFlightSendStableIds.Clear();
            writerState.InFlightRemoveStableId = null;
            writerState.MetadataInFlight = false;
        }

        private ProgressSnapshot BuildProgressSnapshot(
            DiscoveryFacts facts,
            WriterState writerState,
            RunCounters counters)
        {
            SelectionState selectionState = writerState.CreateSelectionState();
            return new ProgressSnapshot(
                _traversalCore.CountCandidateTiles(facts, selectionState),
                counters.ProcessedTiles,
                counters.StreamedMeshes,
                counters.FailedTiles);
        }

        private RunExecutionResult BuildRunExecutionResult(
            DiscoveryFacts facts,
            WriterState writerState,
            RunCounters counters)
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
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);

            return new RunExecutionResult(
                summary,
                visibleTiles,
                selectedTileStableIds,
                _traversalCore.BuildCheckpoint(facts));
        }

        private void UpdateInteractiveState(
            InteractiveExecutionContext? interactiveContext,
            DiscoveryFacts facts,
            WriterState writerState,
            RunCounters counters)
        {
            if (interactiveContext is null)
            {
                return;
            }

            RunExecutionResult state = BuildRunExecutionResult(facts, writerState, counters);
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
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            return command switch
            {
                SendTileWriterCommand send => await ExecuteSendAsync(send, request, cancellationToken).ConfigureAwait(false),
                RemoveTileWriterCommand remove => await ExecuteRemoveAsync(remove, interactiveContext, cancellationToken).ConfigureAwait(false),
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
            try
            {
                SendMeshResult[] results = await Task.WhenAll(command.Content.Meshes.Select(SendPayloadAsync)).ConfigureAwait(false);
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
                    bool rolledBack = await TryRollbackPartialSendAsync(
                        command.Content.Tile.TileId,
                        streamedSlotIds,
                        cancellationToken).ConfigureAwait(false);
                    if (rolledBack)
                    {
                        streamedSlotIds.Clear();
                        streamedMeshCount = 0;
                    }
                }

                return new SendTileCompleted(command.Content, false, streamedMeshCount, streamedSlotIds, firstError);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                if (performanceSummary is not null)
                {
                    performanceSummary.AddSend(Stopwatch.GetElapsedTime(startedAt));
                }
            }

            async Task<SendMeshResult> SendPayloadAsync(PlacedMeshPayload payload)
            {
                try
                {
                    string? slotId = request.Output.DryRun
                        ? null
                        : await _selectedTileProjector.StreamPlacedMeshAsync(payload, cancellationToken).ConfigureAwait(false);
                    return new SendMeshResult(slotId, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new SendMeshResult(null, ex);
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
        private async Task<bool> TryRollbackPartialSendAsync(
            string tileId,
            IReadOnlyList<string> streamedSlotIds,
            CancellationToken cancellationToken)
        {
            bool rolledBack = true;

            foreach (string slotId in streamedSlotIds)
            {
                try
                {
                    await _selectedTileProjector.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception rollbackEx)
                {
                    rolledBack = false;
                    s_rollBackPartialSendFailed(_logger, slotId, tileId, rollbackEx);
                }
            }

            return rolledBack;
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Tile removal captures slot failures to return retry-aware completion state.")]
        private async Task<WriterCompletion> ExecuteRemoveAsync(
            RemoveTileWriterCommand command,
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            RunPerformanceSummary? performanceSummary = _performanceSummary;
            long startedAt = performanceSummary is null ? 0L : Stopwatch.GetTimestamp();
            try
            {
                int failedSlotCount = 0;
                Exception? firstError = null;
                var remainingSlotIds = new List<string>();

                foreach (string slotId in command.SlotIds)
                {
                    try
                    {
                        await _selectedTileProjector.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
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

                return new RemoveTileCompleted(
                    command.StableId,
                    command.TileId,
                    failedSlotCount == 0,
                    failedSlotCount,
                    remainingSlotIds,
                    firstError);
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
            try
            {
                await _selectedTileProjector.SetSessionLicenseCreditAsync(command.LicenseCredit, cancellationToken).ConfigureAwait(false);
                await _selectedTileProjector.SetProgressAsync(
                    request.Output.MeshParentSlotId,
                    command.ProgressValue,
                    command.ProgressText,
                    cancellationToken).ConfigureAwait(false);
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
        }

        private static Task<GoogleTilesAuth> BuildAuthAsync(TileRunRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return Task.FromResult(new GoogleTilesAuth(request.ApiKey, null));
            }

            throw new InvalidOperationException("GOOGLE_MAPS_API_KEY is required for Google Map Tiles API requests.");
        }

        private async Task<IReadOnlyDictionary<string, RetainedTileState>> CommitInteractiveChangesAsync(
            InteractiveExecutionContext interactiveContext,
            IReadOnlySet<string> selectedTileStableIds,
            CancellationToken cancellationToken)
        {
            var failedRetainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);

            if (!interactiveContext.RemoveOutOfRangeTiles)
            {
                return failedRetainedTiles;
            }

            foreach ((string stableId, RetainedTileState retainedTile) in interactiveContext.RetainedTiles)
            {
                if (selectedTileStableIds.Contains(stableId))
                {
                    continue;
                }

                IReadOnlyList<string> failedSlotIds = await TryRemoveSlotsAsync(
                    retainedTile.TileId,
                    retainedTile.SlotIds,
                    cancellationToken).ConfigureAwait(false);
                if (failedSlotIds.Count > 0)
                {
                    failedRetainedTiles[stableId] = retainedTile with
                    {
                        SlotIds = failedSlotIds
                    };
                }
            }

            return failedRetainedTiles;
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Interactive rollback is intentionally tolerant of transient session failures.")]
        private async Task RollbackInteractiveChangesAsync(
            TileRunRequest request,
            InteractiveExecutionContext interactiveContext)
        {
            foreach (string slotId in interactiveContext.GetNewSlotIds())
            {
                try
                {
                    await _selectedTileProjector.RemoveSlotAsync(slotId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    s_rollbackStreamedSlotFailed(_logger, slotId, ex);
                }
            }

            await ApplyFinalLicenseCreditAsync(request, interactiveContext.RetainedTiles, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<InteractiveTileRunResult> FinalizeCanceledInteractiveRunAsync(
            TileRunRequest request,
            InteractiveRunInput interactive,
            InteractiveExecutionContext interactiveContext,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, RetainedTileState> nextRetainedTiles = new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal);
            IReadOnlySet<string> selectedTileStableIds = new HashSet<string>(StringComparer.Ordinal);
            InteractiveRunCheckpoint? checkpoint = interactive.Checkpoint;
            RunSummary summary = new(0, 0, 0, 0);

            if (interactiveContext.LastState is not null)
            {
                nextRetainedTiles = BuildCanceledRetainedTiles(interactive.RetainedTiles, interactiveContext.LastState.VisibleTiles);
                selectedTileStableIds = interactiveContext.LastState.SelectedTileStableIds;
                checkpoint = interactiveContext.LastState.Checkpoint;
                summary = interactiveContext.LastState.Summary;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await ApplyFinalLicenseCreditAsync(request, nextRetainedTiles, cancellationToken).ConfigureAwait(false);
            }
            return new InteractiveTileRunResult(summary, nextRetainedTiles, selectedTileStableIds, checkpoint);
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Best-effort retained slot removal captures failures for caller to handle.")]
        private async Task<IReadOnlyList<string>> TryRemoveSlotsAsync(
            string tileId,
            IReadOnlyList<string> slotIds,
            CancellationToken cancellationToken)
        {
            var failedSlotIds = new List<string>();

            foreach (string slotId in slotIds)
            {
                try
                {
                    await _selectedTileProjector.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    s_removeRetainedSlotFailed(_logger, slotId, tileId, ex);
                    failedSlotIds.Add(slotId);
                }
            }

            return failedSlotIds;
        }

        private async Task ApplyFinalLicenseCreditAsync(
            TileRunRequest request,
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            CancellationToken cancellationToken)
        {
            if (request.Output.DryRun)
            {
                return;
            }

            var aggregator = new LicenseCreditAggregator();
            foreach (RetainedTileState retainedTile in retainedTiles.Values)
            {
                IReadOnlyList<string> owners = LicenseCreditAggregator.ParseOwners(
                    string.IsNullOrWhiteSpace(retainedTile.AssetCopyright)
                        ? []
                        : [retainedTile.AssetCopyright]);
                aggregator.RegisterOrder(owners);
                _ = aggregator.Activate(owners);
            }

            string built = aggregator.BuildCreditString();
            await _selectedTileProjector.SetSessionLicenseCreditAsync(
                string.IsNullOrWhiteSpace(built) ? "Google Maps" : built,
                cancellationToken).ConfigureAwait(false);
        }

        private static Dictionary<string, RetainedTileState> BuildNextRetainedTiles(
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            IReadOnlyDictionary<string, RetainedTileState> visibleTiles,
            IReadOnlySet<string> selectedTileStableIds,
            bool removeOutOfRangeTiles,
            IReadOnlyDictionary<string, RetainedTileState> failedRetainedTiles)
        {
            var next = new Dictionary<string, RetainedTileState>(visibleTiles, StringComparer.Ordinal);

            foreach ((string stableId, RetainedTileState retainedTile) in retainedTiles)
            {
                if (next.ContainsKey(stableId))
                {
                    continue;
                }

                if (failedRetainedTiles.TryGetValue(stableId, out RetainedTileState? failedRetainedTile))
                {
                    next[stableId] = failedRetainedTile;
                    continue;
                }

                if (selectedTileStableIds.Contains(stableId) || removeOutOfRangeTiles)
                {
                    continue;
                }

                next[stableId] = retainedTile;
            }

            return next;
        }

        private static Dictionary<string, RetainedTileState> BuildCanceledRetainedTiles(
            IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
            IReadOnlyDictionary<string, RetainedTileState> visibleTiles)
        {
            return BuildNextRetainedTiles(
                retainedTiles,
                visibleTiles,
                new HashSet<string>(StringComparer.Ordinal),
                removeOutOfRangeTiles: false,
                new Dictionary<string, RetainedTileState>(StringComparer.Ordinal));
        }

        private sealed record RunExecutionResult(
            RunSummary Summary,
            IReadOnlyDictionary<string, RetainedTileState> VisibleTiles,
            IReadOnlySet<string> SelectedTileStableIds,
            InteractiveRunCheckpoint Checkpoint);

        private sealed class RunCounters
        {
            public int ProcessedTiles { get; set; }

            public int StreamedMeshes { get; set; }

            public int FailedTiles { get; set; }

            public long NextPreparedOrder { get; set; }
        }

        private sealed record SendMeshResult(string? SlotId, Exception? Error);

        private sealed class InteractiveExecutionContext
        {
            private readonly Dictionary<string, RetainedTileState> _retainedTiles = new(StringComparer.Ordinal);
            private readonly HashSet<string> _newSlotIds = new(StringComparer.Ordinal);

            public InteractiveExecutionContext(
                IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
                bool removeOutOfRangeTiles)
            {
                RemoveOutOfRangeTiles = removeOutOfRangeTiles;
                foreach ((string stableId, RetainedTileState retainedTile) in retainedTiles)
                {
                    _retainedTiles[stableId] = retainedTile;
                }
            }

            public IReadOnlyDictionary<string, RetainedTileState> RetainedTiles => _retainedTiles;

            public bool RemoveOutOfRangeTiles { get; }

            public RunExecutionResult? LastState { get; private set; }

            public void UpdateState(RunExecutionResult state)
            {
                LastState = state;
            }

            public void TrackNewSlotId(string slotId)
            {
                if (!string.IsNullOrWhiteSpace(slotId))
                {
                    _ = _newSlotIds.Add(slotId);
                }
            }

            public void ForgetNewSlotId(string slotId)
            {
                if (!string.IsNullOrWhiteSpace(slotId))
                {
                    _ = _newSlotIds.Remove(slotId);
                }
            }

            public string[] GetNewSlotIds()
            {
                return [.. _newSlotIds];
            }
        }
    }
}
