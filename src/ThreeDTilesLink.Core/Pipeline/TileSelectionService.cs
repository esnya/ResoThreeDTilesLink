using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class TileSelectionService(
        ITilesSource tilesSource,
        TraversalCore traversalCore,
        SceneReconcilerCore reconcilerCore,
        IGlbMeshExtractor glbMeshExtractor,
        IMeshPlacementService meshPlacementService,
        ISceneSession sceneSession,
        ISceneMetadataSink metadataSink,
        ILicenseCreditPolicy licenseCreditPolicy,
        ILogger logger,
        int maxConcurrentTileProcessing = 1,
        int maxConcurrentWriterSends = 1,
        RunPerformanceSummary? performanceSummary = null) : ITileSelectionService
    {
        private readonly TileSelectionRunOrchestrator _runOrchestrator = new(
            tilesSource,
            traversalCore,
            reconcilerCore,
            glbMeshExtractor,
            meshPlacementService,
            sceneSession,
            metadataSink,
            logger,
            maxConcurrentTileProcessing,
            maxConcurrentWriterSends,
            performanceSummary);

        private readonly TileSelectionInteractiveFinalizer _interactiveFinalizer = new(
            sceneSession,
            metadataSink,
            licenseCreditPolicy,
            logger);

        public async Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            TileSelectionRunExecutionResult result = await _runOrchestrator
                .RunAsync(request, interactiveInput: null, interactiveContext: null, cancellationToken)
                .ConfigureAwait(false);
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

            var interactiveContext = new TileSelectionInteractiveExecutionContext(
                interactive.RetainedTiles,
                interactive.CleanupDebtTiles,
                interactive.RemoveOutOfRangeTiles);

            try
            {
                TileSelectionRunExecutionResult execution = await _runOrchestrator
                    .RunAsync(request, interactive, interactiveContext, cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyDictionary<string, RetainedTileState> failedRetainedTiles = await _interactiveFinalizer
                    .CommitInteractiveChangesAsync(
                        interactiveContext,
                        execution.SelectedTileStableIds,
                        cancellationToken)
                    .ConfigureAwait(false);

                IReadOnlyDictionary<string, RetainedTileState> nextRetainedTiles =
                    TileSelectionInteractiveFinalizer.BuildNextRetainedTiles(
                        interactive.RetainedTiles,
                        execution.VisibleTiles,
                        execution.SelectedTileStableIds,
                        interactive.RemoveOutOfRangeTiles,
                        failedRetainedTiles);

                await _interactiveFinalizer
                    .ApplyFinalLicenseCreditAsync(request, nextRetainedTiles, cancellationToken)
                    .ConfigureAwait(false);
                return new InteractiveTileRunResult(
                    execution.Summary,
                    nextRetainedTiles,
                    execution.SelectedTileStableIds,
                    execution.Checkpoint,
                    execution.CleanupDebtTiles);
            }
            catch (OperationCanceledException)
            {
                return await _interactiveFinalizer
                    .FinalizeCanceledInteractiveRunAsync(
                        request,
                        interactive,
                        interactiveContext,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await _interactiveFinalizer
                    .RollbackInteractiveChangesAsync(request, interactiveContext)
                    .ConfigureAwait(false);
                throw;
            }
        }
    }
}
