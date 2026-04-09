using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal abstract record InteractiveAction;

    internal sealed record ResolveSearchAction(string SearchText, DateTimeOffset RequestedAt) : InteractiveAction;

    internal sealed record CancelActiveRunAction() : InteractiveAction;

    internal sealed record StartRunAction(SelectionInputValues Values, GeoReference SelectionReference, bool Overlaps) : InteractiveAction;

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

            InteractiveLoopState next = ApplyObservedSearch(state, snapshot.SearchText, now);
            next = ApplyObservedValues(next, snapshot.Values, now);

            var actions = new List<InteractiveAction>();

            if (next.PendingSearch is not null &&
                next.PendingSearchChangedAt is not null &&
                now - next.PendingSearchChangedAt.Value >= options.Debounce &&
                !string.Equals(next.LastResolvedSearch, next.PendingSearch, StringComparison.Ordinal))
            {
                actions.Add(new ResolveSearchAction(next.PendingSearch, now));
                next = next with
                {
                    PendingSearch = null,
                    PendingSearchChangedAt = null
                };

                return new InteractiveDecisionResult(next, actions);
            }

            if (next.PendingValues is not null &&
                next.PendingValuesChangedAt is not null)
            {
                bool debounceElapsed = now - next.PendingValuesChangedAt.Value >= options.Debounce;
                bool throttleElapsed = next.LastRunStartedAt == DateTimeOffset.MinValue ||
                    now - next.LastRunStartedAt >= options.Throttle;

                if (debounceElapsed && throttleElapsed)
                {
                    GeoReference selectionReference = createReference(
                        next.PendingValues.Latitude,
                        next.PendingValues.Longitude,
                        heightOffset);
                    var currentFootprint = new InteractiveRangeFootprint(selectionReference, next.PendingValues.RangeM);
                    bool canReuseSlot = next.LastRequestedFootprint is not null &&
                        overlaps(next.LastRequestedFootprint, currentFootprint) &&
                        next.PlacementReference is not null;

                    if (next.ActiveRun is not null)
                    {
                        actions.Add(new CancelActiveRunAction());
                    }

                    actions.Add(new StartRunAction(next.PendingValues, selectionReference, canReuseSlot));
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
            DateTimeOffset now)
        {
            bool resolvedCoordinatesReflected = false;
            if (currentValues is not null && state.AwaitingResolvedCoordinates is not null)
            {
                if (MatchesResolvedCoordinates(currentValues, state.AwaitingResolvedCoordinates))
                {
                    state = state with { AwaitingResolvedCoordinates = null };
                    resolvedCoordinatesReflected = true;
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
