using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

#pragma warning disable CA1822

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class TileRunCoordinator(
        ITilesSource tilesSource,
        TraversalCore traversalCore,
        IContentProcessor contentProcessor,
        IMeshPlacementService meshPlacementService,
        IResoniteSession resoniteSession,
        IGoogleAccessTokenProvider googleAccessTokenProvider,
        ILogger<TileRunCoordinator> logger,
        int maxConcurrentTileProcessing = 1) : ITileRunCoordinator
    {
        private readonly ITilesSource _tilesSource = tilesSource;
        private readonly TraversalCore _traversalCore = traversalCore;
        private readonly IContentProcessor _contentProcessor = contentProcessor;
        private readonly IMeshPlacementService _meshPlacementService = meshPlacementService;
        private readonly IResoniteSession _resoniteSession = resoniteSession;
        private readonly IGoogleAccessTokenProvider _googleAccessTokenProvider = googleAccessTokenProvider;
        private readonly ILogger<TileRunCoordinator> _logger = logger;
        private readonly int _maxConcurrentTileProcessing = maxConcurrentTileProcessing > 0
            ? maxConcurrentTileProcessing
            : throw new ArgumentOutOfRangeException(nameof(maxConcurrentTileProcessing), "Tile content worker count must be positive.");

        public async Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            RunExecutionResult result = await RunCoreAsync(request, interactiveInput: null, interactiveContext: null, cancellationToken).ConfigureAwait(false);
            return result.Summary;
        }

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
                    CancellationToken.None).ConfigureAwait(false);

                IReadOnlyDictionary<string, RetainedTileState> nextRetainedTiles = BuildNextRetainedTiles(
                    interactive.RetainedTiles,
                    execution.VisibleTiles,
                    execution.SelectedTileStableIds,
                    interactive.RemoveOutOfRangeTiles,
                    failedRetainedTiles);

                await ApplyFinalLicenseCreditAsync(request, nextRetainedTiles, CancellationToken.None).ConfigureAwait(false);
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
                    interactiveContext).ConfigureAwait(false);
            }
            catch
            {
                await RollbackInteractiveChangesAsync(request, interactiveContext).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<RunExecutionResult> RunCoreAsync(
            TileRunRequest request,
            InteractiveRunInput? interactiveInput,
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            GoogleTilesAuth auth = await BuildAuthAsync(request, cancellationToken).ConfigureAwait(false);
            if (!request.Output.DryRun && request.Output.ManageConnection)
            {
                _logger.LogInformation("Connecting to Resonite Link at {Host}:{Port}", request.Output.Host, request.Output.Port);
                await _resoniteSession.ConnectAsync(request.Output.Host, request.Output.Port, cancellationToken).ConfigureAwait(false);
            }

            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var discoveryTasks = new Dictionary<string, Task<DiscoveryCompletion>>(StringComparer.Ordinal);
            Task<WriterCompletion>? writerTask = null;

            DiscoveryFacts? facts = null;
            WriterState? writerState = null;
            var counters = new RunCounters();

            try
            {
                _logger.LogInformation("Fetching root tileset from Google Map Tiles API.");
                if (!request.Output.DryRun)
                {
                    try
                    {
                        await _resoniteSession.SetProgressAsync(
                            request.Output.MeshParentSlotId,
                            0f,
                            "Fetching root tileset...",
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "Failed to set initial fetch progress.");
                    }
                }

                Tiles.Tileset rootTileset = await _tilesSource.FetchRootTilesetAsync(auth, cancellationToken).ConfigureAwait(false);
                facts = _traversalCore.Initialize(rootTileset, request, interactiveInput);
                writerState = new WriterState(interactiveInput?.RetainedTiles);
                UpdateInteractiveState(interactiveContext, facts, writerState, counters);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DesiredView desiredView = _traversalCore.ComputeDesiredView(facts, writerState);
                    ProgressSnapshot progress = BuildProgressSnapshot(facts, writerState, counters);

                    int availableDiscoverySlots = System.Math.Max(0, _maxConcurrentTileProcessing - discoveryTasks.Count);
                    foreach (DiscoveryWorkItem work in _traversalCore.PlanDiscovery(facts, writerState, availableDiscoverySlots))
                    {
                        string stableId = work.Tile.StableId!;
                        if (discoveryTasks.ContainsKey(stableId))
                        {
                            continue;
                        }

                        MarkDiscoveryInFlight(facts, work);
                        discoveryTasks.Add(stableId, ExecuteDiscoveryAsync(work, request, auth, workerCts.Token));
                    }

                    if (writerTask is null)
                    {
                        WriterCommand? writerCommand = _traversalCore.PlanWriterCommand(
                            facts,
                            writerState,
                            desiredView,
                            progress,
                            request.Output.DryRun,
                            allowRemoval: discoveryTasks.Count == 0);
                        if (writerCommand is not null)
                        {
                            MarkWriterInFlight(writerState, writerCommand);
                            writerTask = ExecuteWriterCommandAsync(writerCommand, request, interactiveContext, workerCts.Token);
                        }
                    }

                    desiredView = _traversalCore.ComputeDesiredView(facts, writerState);
                    progress = BuildProgressSnapshot(facts, writerState, counters);
                    UpdateInteractiveState(interactiveContext, facts, writerState, counters);

                    bool writerBusy = writerTask is not null;
                    if (_traversalCore.IsSettled(
                        facts,
                        writerState,
                        desiredView,
                        discoveryTasks.Count,
                        writerBusy,
                        progress,
                        request.Output.DryRun))
                    {
                        return BuildRunExecutionResult(facts, writerState, counters);
                    }

                    if (discoveryTasks.Count == 0 && writerTask is null)
                    {
                        throw new InvalidOperationException("Traversal stalled without pending discovery or writer work.");
                    }

                    if (writerTask is null)
                    {
                        Task completedTask = await Task.WhenAny(discoveryTasks.Values).ConfigureAwait(false);
                        if (!completedTask.IsCompleted)
                        {
                            throw new InvalidOperationException("Completed task was not completed.");
                        }
                    }
                    else
                    {
                        Task completedTask = await Task.WhenAny(discoveryTasks.Values.Cast<Task>().Concat([writerTask])).ConfigureAwait(false);
                        if (!completedTask.IsCompleted)
                        {
                            throw new InvalidOperationException("Completed task was not completed.");
                        }
                    }

                    writerTask = await ApplyCompletedWorkAsync(
                        facts,
                        writerState,
                        discoveryTasks,
                        interactiveContext,
                        writerTask,
                        counters).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (facts is not null && writerState is not null)
            {
                workerCts.Cancel();
                writerTask = await ApplyCompletedWorkAsync(
                    facts,
                    writerState,
                    discoveryTasks,
                    interactiveContext,
                    writerTask,
                    counters).ConfigureAwait(false);
                writerTask = await DrainOutstandingWorkAsync(
                    facts,
                    writerState,
                    discoveryTasks,
                    interactiveContext,
                    writerTask,
                    counters).ConfigureAwait(false);
                UpdateInteractiveState(interactiveContext, facts, writerState, counters);
                throw;
            }
            finally
            {
                workerCts.Cancel();
                await ObserveOutstandingTasksAsync(discoveryTasks.Values, writerTask).ConfigureAwait(false);

                if (!request.Output.DryRun && request.Output.ManageConnection)
                {
                    await _resoniteSession.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        private async Task<Task<WriterCompletion>?> ApplyCompletedWorkAsync(
            DiscoveryFacts facts,
            WriterState writerState,
            Dictionary<string, Task<DiscoveryCompletion>> discoveryTasks,
            InteractiveExecutionContext? interactiveContext,
            Task<WriterCompletion>? writerTask,
            RunCounters counters)
        {
            foreach ((string stableId, Task<DiscoveryCompletion> task) in discoveryTasks
                         .Where(static pair => pair.Value.IsCompleted)
                         .ToArray())
            {
                _ = discoveryTasks.Remove(stableId);
                try
                {
                    DiscoveryCompletion completion = await task.ConfigureAwait(false);
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

            if (writerTask is null || !writerTask.IsCompleted)
            {
                return writerTask;
            }

            try
            {
                WriterCompletion completion = await writerTask.ConfigureAwait(false);
                LogWriterCompletion(completion);
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
                _traversalCore.ApplyWriterCompletion(
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
            finally
            {
                writerTask = null;
            }

            return writerTask;
        }

        private async Task<Task<WriterCompletion>?> DrainOutstandingWorkAsync(
            DiscoveryFacts facts,
            WriterState writerState,
            Dictionary<string, Task<DiscoveryCompletion>> discoveryTasks,
            InteractiveExecutionContext? interactiveContext,
            Task<WriterCompletion>? writerTask,
            RunCounters counters)
        {
            while (discoveryTasks.Count > 0 || writerTask is not null)
            {
                if (writerTask is null)
                {
                    _ = await Task.WhenAny(discoveryTasks.Values).ConfigureAwait(false);
                }
                else
                {
                    _ = await Task.WhenAny(discoveryTasks.Values.Cast<Task>().Concat([writerTask])).ConfigureAwait(false);
                }

                writerTask = await ApplyCompletedWorkAsync(
                    facts,
                    writerState,
                    discoveryTasks,
                    interactiveContext,
                    writerTask,
                    counters).ConfigureAwait(false);
            }

            return writerTask;
        }

        private async Task ObserveOutstandingTasksAsync(
            IEnumerable<Task<DiscoveryCompletion>> discoveryTasks,
            Task<WriterCompletion>? writerTask)
        {
            IEnumerable<Task> outstanding = writerTask is null
                ? discoveryTasks
                : discoveryTasks.Cast<Task>().Concat([writerTask]);

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
                    _logger.LogInformation(
                        "Loaded nested tileset for tile {TileId} from {Uri}.",
                        nested.Tile.TileId,
                        nested.Tile.ContentUri);
                    break;

                case TilePrepared prepared:
                    _logger.LogInformation(
                        "Prepared tile {TileId} from {Uri} with {MeshCount} meshes.",
                        prepared.Tile.TileId,
                        prepared.Tile.ContentUri,
                        prepared.Content.Meshes.Count);
                    break;

                case DiscoverySkipped skipped when skipped.Error is not null:
                    _logger.LogWarning(
                        skipped.Error,
                        "Skipped tile {TileId} from {Uri} after fetch/process error.",
                        skipped.Tile.TileId,
                        skipped.Tile.ContentUri);
                    break;

                case DiscoverySkipped skipped:
                    _logger.LogInformation(
                        "Skipped tile {TileId} from {Uri}: {Reason}",
                        skipped.Tile.TileId,
                        skipped.Tile.ContentUri,
                        skipped.Reason ?? "no reason");
                    break;

                case DiscoveryFailed failed:
                    _logger.LogWarning(
                        failed.Error,
                        "Failed to discover tile {TileId} from {Uri}.",
                        failed.Tile.TileId,
                        failed.Tile.ContentUri);
                    break;
            }
        }

        private void LogWriterCompletion(WriterCompletion completion)
        {
            switch (completion)
            {
                case SendTileCompleted sent when sent.Succeeded:
                    _logger.LogInformation(
                        "Streamed tile {TileId}: meshes={MeshCount}, slots={SlotCount}.",
                        sent.Content.Tile.TileId,
                        sent.StreamedMeshCount,
                        sent.SlotIds.Count);
                    break;

                case SendTileCompleted sent:
                    _logger.LogWarning(
                        sent.Error,
                        "Failed to stream tile {TileId}: streamedMeshes={MeshCount}, slots={SlotCount}.",
                        sent.Content.Tile.TileId,
                        sent.StreamedMeshCount,
                        sent.SlotIds.Count);
                    break;

                case RemoveTileCompleted removed when removed.Succeeded:
                    _logger.LogInformation(
                        "Removed tile {TileId}.",
                        removed.TileId);
                    break;

                case RemoveTileCompleted removed:
                    _logger.LogWarning(
                        removed.Error,
                        "Failed to fully remove tile {TileId}: remainingSlots={RemainingSlotCount}.",
                        removed.TileId,
                        removed.RemainingSlotIds.Count);
                    break;

                case SyncSessionMetadataCompleted metadata when metadata.Succeeded:
                    _logger.LogInformation(
                        "Updated session metadata: progress={Progress:P0}, text={ProgressText}.",
                        metadata.ProgressValue,
                        metadata.ProgressText);
                    break;

                case SyncSessionMetadataCompleted metadata:
                    _logger.LogWarning(
                        metadata.Error,
                        "Failed to update session metadata: progress={Progress:P0}, text={ProgressText}.",
                        metadata.ProgressValue,
                        metadata.ProgressText);
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
                    writerState.InFlightSendStableId = send.Content.Tile.StableId;
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
            writerState.InFlightSendStableId = null;
            writerState.InFlightRemoveStableId = null;
            writerState.MetadataInFlight = false;
        }

        private ProgressSnapshot BuildProgressSnapshot(
            DiscoveryFacts facts,
            WriterState writerState,
            RunCounters counters)
        {
            return new ProgressSnapshot(
                _traversalCore.CountCandidateTiles(facts, writerState),
                counters.ProcessedTiles,
                counters.StreamedMeshes,
                counters.FailedTiles);
        }

        private RunExecutionResult BuildRunExecutionResult(
            DiscoveryFacts facts,
            WriterState writerState,
            RunCounters counters)
        {
            var summary = new RunSummary(
                _traversalCore.CountCandidateTiles(facts, writerState),
                counters.ProcessedTiles,
                counters.StreamedMeshes,
                counters.FailedTiles);
            IReadOnlySet<string> selectedTileStableIds = facts.Branches.Keys.ToHashSet(StringComparer.Ordinal);
            IReadOnlyDictionary<string, RetainedTileState> visibleTiles = _traversalCore.BuildVisibleTiles(writerState)
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
                            _meshPlacementService.Place(
                                work.Tile,
                                renderable.Meshes,
                                request.PlacementReference,
                                request.Output.MeshParentSlotId),
                            renderable.AssetCopyright)),
                    SkippedContentProcessResult skipped => new DiscoverySkipped(work.Tile, skipped.Reason),
                    _ => throw new InvalidOperationException($"Unsupported content process result: {content.GetType().Name}")
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
                SyncSessionMetadataWriterCommand metadata => await ExecuteSyncMetadataAsync(request, metadata, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported writer command type: {command.GetType().Name}")
            };
        }

        private async Task<WriterCompletion> ExecuteSendAsync(
            SendTileWriterCommand command,
            TileRunRequest request,
            CancellationToken cancellationToken)
        {
            var streamedSlotIds = new List<string>();
            int streamedMeshCount = 0;

            try
            {
                foreach (PlacedMeshPayload payload in command.Content.Meshes)
                {
                    string? slotId = request.Output.DryRun
                        ? null
                        : await _resoniteSession.StreamPlacedMeshAsync(payload, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(slotId))
                    {
                        streamedSlotIds.Add(slotId);
                    }

                    streamedMeshCount++;
                }

                return new SendTileCompleted(command.Content, true, streamedMeshCount, streamedSlotIds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
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

                return new SendTileCompleted(command.Content, false, streamedMeshCount, streamedSlotIds, ex);
            }
        }

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
                    await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception rollbackEx)
                {
                    rolledBack = false;
                    _logger.LogWarning(
                        rollbackEx,
                        "Failed to roll back partially streamed slot {SlotId} for tile {TileId}.",
                        slotId,
                        tileId);
                }
            }

            return rolledBack;
        }

        private async Task<WriterCompletion> ExecuteRemoveAsync(
            RemoveTileWriterCommand command,
            InteractiveExecutionContext? interactiveContext,
            CancellationToken cancellationToken)
        {
            int failedSlotCount = 0;
            Exception? firstError = null;
            var remainingSlotIds = new List<string>();
            IReadOnlyList<string> immediateSlotIds = command.SlotIds;

            if (interactiveContext is not null)
            {
                immediateSlotIds = interactiveContext.StageRetainedRemovals(command.TileId, command.SlotIds);
            }

            foreach (string slotId in immediateSlotIds)
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

            return new RemoveTileCompleted(
                command.StableId,
                command.TileId,
                failedSlotCount == 0,
                failedSlotCount,
                remainingSlotIds,
                firstError);
        }

        private async Task<WriterCompletion> ExecuteSyncMetadataAsync(
            TileRunRequest request,
            SyncSessionMetadataWriterCommand command,
            CancellationToken cancellationToken)
        {
            try
            {
                await _resoniteSession.SetSessionLicenseCreditAsync(command.LicenseCredit, cancellationToken).ConfigureAwait(false);
                await _resoniteSession.SetProgressAsync(
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

        private async Task<GoogleTilesAuth> BuildAuthAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return new GoogleTilesAuth(request.ApiKey, null);
            }

            string token = await _googleAccessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return new GoogleTilesAuth(null, token);
        }

        private async Task<IReadOnlyDictionary<string, RetainedTileState>> CommitInteractiveChangesAsync(
            InteractiveExecutionContext interactiveContext,
            IReadOnlySet<string> selectedTileStableIds,
            CancellationToken cancellationToken)
        {
            var failedRetainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal);

            foreach (RetainedTileRemoval stagedRemoval in interactiveContext.GetStagedRetainedRemovals())
            {
                IReadOnlyList<string> failedSlotIds = await TryRemoveSlotsAsync(
                    stagedRemoval.TileId,
                    stagedRemoval.SlotIds,
                    cancellationToken).ConfigureAwait(false);
                if (failedSlotIds.Count > 0)
                {
                    failedRetainedTiles[stagedRemoval.StateId] = stagedRemoval.RetainedTile with
                    {
                        SlotIds = failedSlotIds
                    };
                }
            }

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

        private async Task RollbackInteractiveChangesAsync(
            TileRunRequest request,
            InteractiveExecutionContext interactiveContext)
        {
            foreach (string slotId in interactiveContext.GetNewSlotIds())
            {
                try
                {
                    await _resoniteSession.RemoveSlotAsync(slotId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to rollback newly streamed slot {SlotId}.", slotId);
                }
            }

            await ApplyFinalLicenseCreditAsync(request, interactiveContext.RetainedTiles, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<InteractiveTileRunResult> FinalizeCanceledInteractiveRunAsync(
            TileRunRequest request,
            InteractiveRunInput interactive,
            InteractiveExecutionContext interactiveContext)
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

            await ApplyFinalLicenseCreditAsync(request, nextRetainedTiles, CancellationToken.None).ConfigureAwait(false);
            return new InteractiveTileRunResult(summary, nextRetainedTiles, selectedTileStableIds, checkpoint);
        }

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
                    await _resoniteSession.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove retained slot {SlotId} for tile {TileId}.", slotId, tileId);
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
            await _resoniteSession.SetSessionLicenseCreditAsync(
                string.IsNullOrWhiteSpace(built) ? "Google Maps" : built,
                cancellationToken).ConfigureAwait(false);
        }

        private static IReadOnlyDictionary<string, RetainedTileState> BuildNextRetainedTiles(
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

        private static IReadOnlyDictionary<string, RetainedTileState> BuildCanceledRetainedTiles(
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

        private sealed class InteractiveExecutionContext
        {
            private readonly Dictionary<string, RetainedTileState> _retainedTiles = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _stableIdByRetainedSlotId = new(StringComparer.Ordinal);
            private readonly Dictionary<string, RetainedTileRemoval> _stagedRetainedRemovals = new(StringComparer.Ordinal);
            private readonly HashSet<string> _newSlotIds = new(StringComparer.Ordinal);

            public InteractiveExecutionContext(
                IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
                bool removeOutOfRangeTiles)
            {
                RemoveOutOfRangeTiles = removeOutOfRangeTiles;
                foreach ((string stableId, RetainedTileState retainedTile) in retainedTiles)
                {
                    _retainedTiles[stableId] = retainedTile;
                    foreach (string slotId in retainedTile.SlotIds)
                    {
                        _stableIdByRetainedSlotId[slotId] = stableId;
                    }
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

            public IReadOnlyList<string> GetNewSlotIds()
            {
                return _newSlotIds.ToArray();
            }

            public IReadOnlyList<string> StageRetainedRemovals(string tileId, IReadOnlyList<string> slotIds)
            {
                var immediateSlotIds = new List<string>(slotIds.Count);

                foreach (string slotId in slotIds)
                {
                    if (!_stableIdByRetainedSlotId.TryGetValue(slotId, out string? stableId) ||
                        !_retainedTiles.TryGetValue(stableId, out RetainedTileState? retainedTile))
                    {
                        immediateSlotIds.Add(slotId);
                        continue;
                    }

                    if (!_stagedRetainedRemovals.TryGetValue(stableId, out RetainedTileRemoval? removal))
                    {
                        removal = new RetainedTileRemoval(stableId, tileId, retainedTile);
                        _stagedRetainedRemovals[stableId] = removal;
                    }

                    removal.AddSlot(slotId);
                }

                return immediateSlotIds;
            }

            public IReadOnlyCollection<RetainedTileRemoval> GetStagedRetainedRemovals()
            {
                return _stagedRetainedRemovals.Values.ToArray();
            }
        }

        private sealed class RetainedTileRemoval(string stateId, string tileId, RetainedTileState retainedTile)
        {
            private readonly HashSet<string> _slotIds = new(StringComparer.Ordinal);

            public string StateId { get; } = stateId;

            public string TileId { get; } = tileId;

            public RetainedTileState RetainedTile { get; } = retainedTile;

            public IReadOnlyList<string> SlotIds => _slotIds.ToArray();

            public void AddSlot(string slotId)
            {
                if (!string.IsNullOrWhiteSpace(slotId))
                {
                    _ = _slotIds.Add(slotId);
                }
            }
        }
    }
}
