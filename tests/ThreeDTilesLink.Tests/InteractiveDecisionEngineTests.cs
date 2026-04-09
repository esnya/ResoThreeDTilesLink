using FluentAssertions;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.Tests
{
    public sealed class InteractiveDecisionEngineTests
    {
        [Fact]
        public void Evaluate_DoesNotResolveSearch_BeforeDebounceElapses()
        {
            InteractiveLoopState state = InteractiveLoopState.CreateInitial() with
            {
                PendingSearch = "Shibuya",
                PendingSearchChangedAt = DateTimeOffset.UnixEpoch,
                LastObservedSearch = "Shibuya"
            };

            InteractiveDecisionResult result = Evaluate(
                state,
                new SelectionInputSnapshot("Shibuya", null),
                debounce: TimeSpan.FromSeconds(5),
                throttle: TimeSpan.FromSeconds(1),
                now: DateTimeOffset.UnixEpoch.AddSeconds(1));

            _ = result.Actions.Should().BeEmpty();
            _ = result.State.PendingSearch.Should().Be("Shibuya");
            _ = result.State.PendingSearchChangedAt.Should().Be(DateTimeOffset.UnixEpoch);
        }

        [Fact]
        public void Evaluate_ResolvesSearch_AfterDebounceElapses_AndSearchChanged()
        {
            InteractiveLoopState state = InteractiveLoopState.CreateInitial() with
            {
                PendingSearch = "Shibuya",
                PendingSearchChangedAt = DateTimeOffset.UnixEpoch,
                LastObservedSearch = "Shibuya"
            };

            InteractiveDecisionResult result = Evaluate(
                state,
                new SelectionInputSnapshot("Shibuya", null),
                debounce: TimeSpan.FromSeconds(1),
                throttle: TimeSpan.FromSeconds(1),
                now: DateTimeOffset.UnixEpoch.AddSeconds(2));

            _ = result.Actions.Should().ContainSingle().Which.Should().BeOfType<ResolveSearchAction>()
                .Which.SearchText.Should().Be("Shibuya");
            _ = result.State.PendingSearch.Should().BeNull();
            _ = result.State.PendingSearchChangedAt.Should().BeNull();
        }

        [Fact]
        public void Evaluate_IgnoresSearchResolution_WhenResolvedCoordinatesDoNotMatchObservedValues()
        {
            InteractiveLoopState state = InteractiveLoopState.CreateInitial() with
            {
                AwaitingResolvedCoordinates = new LocationSearchResult("resolved", 35.0, 139.0),
                LastObservedValues = new SelectionInputValues(35.1f, 139.1f, 400f),
                LastObservedSearch = "Shibuya"
            };

            InteractiveDecisionResult result = Evaluate(
                state,
                new SelectionInputSnapshot("Shibuya", new SelectionInputValues(35.1f, 139.1f, 400f)),
                debounce: TimeSpan.FromSeconds(1),
                throttle: TimeSpan.FromSeconds(1),
                now: DateTimeOffset.UnixEpoch.AddSeconds(2));

            _ = result.Actions.Should().BeEmpty();
            _ = result.State.AwaitingResolvedCoordinates.Should().NotBeNull();
            _ = result.State.PendingValues.Should().BeNull();
            _ = result.State.PendingValuesChangedAt.Should().BeNull();
        }

        [Fact]
        public void Evaluate_ReflectsResolvedCoordinates_WhenObservedValuesMatchSearchResult()
        {
            SelectionInputValues values = new(35.0f, 139.0f, 400f);
            InteractiveLoopState state = InteractiveLoopState.CreateInitial() with
            {
                AwaitingResolvedCoordinates = new LocationSearchResult("resolved", 35.0, 139.0),
                LastObservedValues = values
            };

            InteractiveDecisionResult result = Evaluate(
                state,
                new SelectionInputSnapshot(null, values),
                debounce: TimeSpan.FromSeconds(1),
                throttle: TimeSpan.FromSeconds(1),
                now: DateTimeOffset.UnixEpoch.AddSeconds(2));

            _ = result.Actions.Should().BeEmpty();
            _ = result.State.AwaitingResolvedCoordinates.Should().BeNull();
            _ = result.State.PendingValues.Should().Be(values);
            _ = result.State.PendingValuesChangedAt.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(2));
        }

        [Fact]
        public void Evaluate_StartsRun_WhenSelectionValuesAreReady()
        {
            InteractiveLoopState state = InteractiveLoopState.CreateInitial() with
            {
                LastObservedValues = new SelectionInputValues(35f, 139f, 400f),
                PendingValues = new SelectionInputValues(35f, 139f, 400f),
                PendingValuesChangedAt = DateTimeOffset.UnixEpoch,
                LastRunStartedAt = DateTimeOffset.UnixEpoch.AddSeconds(-10),
                LastRequestedFootprint = new InteractiveRangeFootprint(new GeoReference(35d, 139d, 123d), 400d),
                PlacementReference = new GeoReference(35d, 139d, 123d)
            };

            InteractiveDecisionResult result = Evaluate(
                state,
                new SelectionInputSnapshot(null, new SelectionInputValues(35f, 139f, 400f)),
                debounce: TimeSpan.FromSeconds(1),
                throttle: TimeSpan.FromSeconds(1),
                now: DateTimeOffset.UnixEpoch.AddSeconds(2));

            _ = result.Actions.Should().ContainSingle();
            StartRunAction startAction = result.Actions[0].Should().BeOfType<StartRunAction>().Subject;
            _ = startAction.Values.Should().Be(new SelectionInputValues(35f, 139f, 400f));
            _ = startAction.Overlaps.Should().BeTrue();
            _ = startAction.SelectionReference.Should().Be(new GeoReference(35d, 139d, 123d));
            _ = result.State.PendingValues.Should().BeNull();
            _ = result.State.PendingValuesChangedAt.Should().BeNull();
            _ = result.State.LastRequestedFootprint.Should().NotBeNull();
            _ = result.State.LastRequestedFootprint!.Reference.Should().Be(new GeoReference(35d, 139d, 123d));
            _ = result.State.LastRunStartedAt.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(2));
        }

        private static InteractiveDecisionResult Evaluate(
            InteractiveLoopState state,
            SelectionInputSnapshot snapshot,
            TimeSpan debounce,
            TimeSpan throttle,
            DateTimeOffset now)
        {
            return InteractiveDecisionEngine.Evaluate(
                state,
                snapshot,
                new WatchOptions(
                    TimeSpan.FromMilliseconds(250),
                    debounce,
                    throttle,
                    new WatchConfiguration("lat", "lon", "range", "search")),
                heightOffset: 123d,
                now,
                static (lat, lon, height) => new GeoReference(lat, lon, height),
                static (_, _) => true);
        }
    }
}
