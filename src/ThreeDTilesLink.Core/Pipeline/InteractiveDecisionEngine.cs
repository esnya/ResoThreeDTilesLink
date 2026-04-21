using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal abstract record InteractiveAction;

    internal sealed record ResolveSearchAction(string SearchText) : InteractiveAction;

    internal sealed record CancelActiveRunAction() : InteractiveAction;

    internal sealed record StartRunAction(
        SelectionInputValues Values,
        GeoReference SelectionReference,
        bool Overlaps,
        bool ReuseCheckpoint) : InteractiveAction;

    internal sealed record InteractiveDecisionResult(
        InteractiveLoopState State,
        IReadOnlyList<InteractiveAction> Actions);

    internal static class InteractiveDecisionEngine
    {
        internal static InteractiveDecisionResult Evaluate(
            InteractiveLoopState state,
            SelectionInputSnapshot snapshot,
            WatchOptions options,
            double heightOffset,
            DateTimeOffset now,
            Func<double, double, double, GeoReference> createReference,
            Func<InteractiveRangeFootprint, InteractiveRangeFootprint, bool> overlaps)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(createReference);
            ArgumentNullException.ThrowIfNull(overlaps);

            var actions = new List<InteractiveAction>();
            InteractiveLoopState next = ApplyObservedSearch(state, snapshot.SearchText, now);
            next = ApplyObservedValues(next, snapshot.Values, snapshot.HasInvalidValues, now);

            if (next.PendingSearch is not null &&
                next.PendingSearchChangedAt is not null &&
                now - next.PendingSearchChangedAt.Value >= options.Debounce &&
                !string.Equals(next.LastResolvedSearch, next.PendingSearch, StringComparison.Ordinal))
            {
                actions.Add(new ResolveSearchAction(next.PendingSearch));
                next = next with
                {
                    PendingSearch = null,
                    PendingSearchChangedAt = null
                };

                return new InteractiveDecisionResult(next, actions);
            }

            if (next.PendingSearch is not null &&
                string.Equals(next.LastResolvedSearch, next.PendingSearch, StringComparison.Ordinal))
            {
                next = next with
                {
                    PendingSearch = null,
                    PendingSearchChangedAt = null
                };
            }

            if (next.PendingSearch is null &&
                next.PendingValues is not null &&
                next.PendingValuesChangedAt is not null)
            {
                bool debounceElapsed = now - next.PendingValuesChangedAt.Value >= options.Debounce;
                bool throttleElapsed = next.ActiveRun is not null ||
                    next.LastRunStartedAt == DateTimeOffset.MinValue ||
                    now - next.LastRunStartedAt >= options.Throttle;

                if (debounceElapsed && throttleElapsed)
                {
                    GeoReference selectionReference = createReference(
                        next.PendingValues.Latitude,
                        next.PendingValues.Longitude,
                        heightOffset);
                    var currentFootprint = new InteractiveRangeFootprint(selectionReference, next.PendingValues.RangeM);
                    bool rangeChanged = next.LastRequestedFootprint is not null &&
                        System.Math.Abs(next.LastRequestedFootprint.RangeM - currentFootprint.RangeM) > 0.1d;
                    bool overlapsPrevious = next.LastRequestedFootprint is not null &&
                        overlaps(next.LastRequestedFootprint, currentFootprint) &&
                        next.PlacementReference is not null;
                    bool reuseCheckpoint = overlapsPrevious && !rangeChanged;

                    if (next.ActiveRun is not null)
                    {
                        actions.Add(new CancelActiveRunAction());
                    }

                    actions.Add(new StartRunAction(next.PendingValues, selectionReference, overlapsPrevious, reuseCheckpoint));
                    next = next with
                    {
                        LastRequestedFootprint = currentFootprint,
                        LastRunStartedAt = now,
                        PendingValues = null,
                        PendingValuesChangedAt = null
                    };
                }
            }

            return new InteractiveDecisionResult(next, actions);
        }

        private static InteractiveLoopState ApplyObservedSearch(
            InteractiveLoopState state,
            string? currentSearch,
            DateTimeOffset now)
        {
            if (string.Equals(state.LastObservedSearch, currentSearch, StringComparison.Ordinal))
            {
                return state;
            }

            if (currentSearch is null)
            {
                return state with
                {
                    LastObservedSearch = null,
                    PendingSearch = null,
                    PendingSearchChangedAt = null,
                    LastResolvedSearch = null,
                    AwaitingResolvedCoordinates = null
                };
            }

            return state with
            {
                LastObservedSearch = currentSearch,
                PendingSearch = currentSearch,
                PendingSearchChangedAt = now
            };
        }

        private static InteractiveLoopState ApplyObservedValues(
            InteractiveLoopState state,
            SelectionInputValues? currentValues,
            bool hasInvalidValues,
            DateTimeOffset now)
        {
            if (hasInvalidValues)
            {
                return state with
                {
                    LastObservedValues = null,
                    PendingValues = null,
                    PendingValuesChangedAt = null
                };
            }

            bool resolvedCoordinatesReflected = false;
            if (currentValues is not null && state.AwaitingResolvedCoordinates is not null)
            {
                if (MatchesResolvedCoordinates(currentValues, state.AwaitingResolvedCoordinates))
                {
                    state = state with { AwaitingResolvedCoordinates = null };
                    resolvedCoordinatesReflected = true;
                }
                else if (SelectionInputReader.HasMeaningfulChange(state.LastObservedValues, currentValues))
                {
                    state = state with { AwaitingResolvedCoordinates = null };
                }
                else
                {
                    currentValues = null;
                }
            }

            if (currentValues is null)
            {
                return state;
            }

            bool hasMeaningfulChange = SelectionInputReader.HasMeaningfulChange(state.LastObservedValues, currentValues);
            bool shouldStartInitialRunAfterSearchReflection =
                resolvedCoordinatesReflected &&
                state.LastRequestedFootprint is null &&
                state.ActiveRun is null;
            if (!hasMeaningfulChange && !shouldStartInitialRunAfterSearchReflection)
            {
                return state;
            }

            return state with
            {
                LastObservedValues = currentValues,
                PendingValues = currentValues,
                PendingValuesChangedAt = now
            };
        }

        private static bool MatchesResolvedCoordinates(SelectionInputValues values, LocationSearchResult resolved)
        {
            const float coordinateTolerance = 1e-5f;
            return MathF.Abs(values.Latitude - (float)resolved.Latitude) <= coordinateTolerance &&
                   MathF.Abs(values.Longitude - (float)resolved.Longitude) <= coordinateTolerance;
        }
    }
}
