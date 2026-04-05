using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed partial class InteractiveRunSupervisor
    {
        private readonly IWatchStore _watchStore;
        private readonly ICoordinateTransformer _coordinateTransformer;
        private readonly IClock _clock;
        private readonly SelectionInputReader _selectionInputReader;
        private readonly InteractiveActionApplier _actionApplier;
        private readonly ILogger<InteractiveRunSupervisor> _logger;

        internal InteractiveRunSupervisor(
            ITileSelectionService tileRunCoordinator,
            IResoniteSession resoniteSession,
            IWatchStore watchStore,
            ISearchResolver searchResolver,
            ICoordinateTransformer coordinateTransformer,
            IClock clock,
            SelectionInputReader selectionInputReader,
            ILogger<InteractiveRunSupervisor> logger)
        {
            _watchStore = watchStore;
            _coordinateTransformer = coordinateTransformer;
            _clock = clock;
            _selectionInputReader = selectionInputReader;
            _logger = logger;
            _actionApplier = new InteractiveActionApplier(
                tileRunCoordinator,
                resoniteSession,
                watchStore,
                searchResolver,
                coordinateTransformer,
                logger);
        }

        public async Task RunAsync(InteractiveRunRequest options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            SelectionInputReader.ValidateIntervals(options.Watch);

            InteractiveLoopState state = InteractiveLoopState.CreateInitial();

            try
            {
                Log.ConnectingToResonite(_logger, options.ResoniteHost, options.ResonitePort);
                await _actionApplier.ConnectAsync(options.ResoniteHost, options.ResonitePort, cancellationToken).ConfigureAwait(false);
                state = state with { Connected = true };

                WatchBinding watchBinding = await _watchStore.CreateWatchAsync(options.Watch.Configuration, cancellationToken).ConfigureAwait(false);
                state = state with { WatchBinding = watchBinding };
                Log.WatchBindingAttached(
                    _logger,
                    watchBinding.SlotId,
                    watchBinding.OwnsSlot,
                    options.Watch.Configuration.LatitudeVariablePath,
                    options.Watch.Configuration.LongitudeVariablePath,
                    options.Watch.Configuration.RangeVariablePath,
                    options.Watch.Configuration.SearchVariablePath);

                while (!cancellationToken.IsCancellationRequested)
                {
                    state = await _actionApplier.FinalizeCompletedRunAsync(state, cancellationToken).ConfigureAwait(false);

                    SelectionInputSnapshot snapshot = await _selectionInputReader.ReadAsync(state.WatchBinding!, cancellationToken).ConfigureAwait(false);
                    DateTimeOffset now = _clock.UtcNow;
                    InteractiveLoopState previousState = state;
                    InteractiveDecisionResult decision = InteractiveDecisionEngine.Evaluate(
                        state,
                        snapshot,
                        options.Watch,
                        options.HeightOffsetM,
                        now,
                        Overlaps);
                    state = decision.State;
                    LogInputChanges(previousState, state);
                    state = await _actionApplier.ApplyAsync(state, decision.Actions, options, cancellationToken).ConfigureAwait(false);

                    await _clock.Delay(options.Watch.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                state = await _actionApplier.DisconnectAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }

        private void LogInputChanges(InteractiveLoopState previousState, InteractiveLoopState state)
        {
            if (!string.Equals(previousState.LastObservedSearch, state.LastObservedSearch, StringComparison.Ordinal) &&
                state.LastObservedSearch is not null)
            {
                Log.SearchQueryChanged(_logger, state.LastObservedSearch);
            }

            if (state.LastObservedValues is not null &&
                SelectionInputReader.HasMeaningfulChange(previousState.LastObservedValues, state.LastObservedValues))
            {
                Log.SelectionInputChanged(
                    _logger,
                    state.LastObservedValues.Latitude,
                    state.LastObservedValues.Longitude,
                    state.LastObservedValues.RangeM);
            }
        }

        private bool Overlaps(InteractiveRangeFootprint previous, InteractiveRangeFootprint current)
        {
            Vector3d currentEcef = _coordinateTransformer.GeographicToEcef(
                current.Reference.Latitude,
                current.Reference.Longitude,
                current.Reference.HeightM);
            Vector3d currentEnu = _coordinateTransformer.EcefToEnu(currentEcef, previous.Reference);
            double overlapThreshold = previous.RangeM + current.RangeM;
            return System.Math.Abs(currentEnu.X) <= overlapThreshold &&
                   System.Math.Abs(currentEnu.Y) <= overlapThreshold;
        }

        internal static partial class Log
        {
            [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Connecting to Resonite Link at {Host}:{Port}.")]
            public static partial void ConnectingToResonite(ILogger logger, string host, int port);

            [LoggerMessage(
                EventId = 2,
                Level = LogLevel.Information,
                Message = "Watch attached: slotId={SlotId} ownsSlot={OwnsSlot} lat={LatPath} lon={LonPath} range={RangePath} search={SearchPath}")]
            public static partial void WatchBindingAttached(ILogger logger, string slotId, bool ownsSlot, string latPath, string lonPath, string rangePath, string searchPath);

            [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Search query changed: {Query}")]
            public static partial void SearchQueryChanged(ILogger logger, string query);

            [LoggerMessage(
                EventId = 4,
                Level = LogLevel.Information,
                Message = "Selection input changed: lat={Lat:F7} lon={Lon:F7} range={Range:F1}m")]
            public static partial void SelectionInputChanged(ILogger logger, double lat, double lon, double range);

            [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Search resolved: query={Query} name={Name} lat={Latitude:F7} lon={Longitude:F7}")]
            public static partial void SearchResolved(ILogger logger, string query, string name, double latitude, double longitude);

            [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Failed to resolve search query: {Query}")]
            public static partial void SearchResolutionFailed(ILogger logger, Exception exception, string query);

            [LoggerMessage(
                EventId = 7,
                Level = LogLevel.Information,
                Message = "Run started: slot={SlotId} selectionLat={Lat:F7} selectionLon={Lon:F7} placementLat={PlacementLat:F7} placementLon={PlacementLon:F7} range={Range:F1}m overlap={Overlap}")]
            public static partial void RunStarted(
                ILogger logger,
                string slotId,
                double lat,
                double lon,
                double placementLat,
                double placementLon,
                double range,
                bool overlap);

            [LoggerMessage(
                EventId = 8,
                Level = LogLevel.Information,
                Message = "Run completed: retained={Retained} candidate={Candidate} processed={Processed} streamed={Streamed} failed={Failed}")]
            public static partial void RunCompleted(
                ILogger logger,
                int retained,
                int candidate,
                int processed,
                int streamed,
                int failed);

            [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Run failed.")]
            public static partial void RunFailed(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Run finished with error while superseding.")]
            public static partial void RunSupersededFailed(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Failed to remove slot {SlotId}.")]
            public static partial void SlotRemovalFailed(ILogger logger, Exception exception, string slotId);

            [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Failed to disconnect Resonite Link cleanly.")]
            public static partial void DisconnectFailed(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Search query ignored because GOOGLE_MAPS_API_KEY is not set: query={Query}")]
            public static partial void SearchIgnored(ILogger logger, string query);

            [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Search query returned no result: query={Query}")]
            public static partial void SearchNoResult(ILogger logger, string query);
        }
    }
}
