using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Resonite;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class InteractiveRunSupervisorTests
    {
        [Fact]
        public async Task RunAsync_StartsRun_WhenSelectionInputValuesChange()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>([new SelectionInputValues(35f, 139f, 400f), new SelectionInputValues(35f, 139f, 400f)])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 3 };
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().ContainSingle();
            _ = coordinator.Requests[0].Traversal.RangeM.Should().Be(400d);
            _ = coordinator.Requests[0].Output.ManageConnection.Should().BeFalse();
            _ = coordinator.Requests[0].Output.MeshParentSlotId.Should().BeNull();
        }

        [Fact]
        public async Task RunAsync_KeepsPlacementReference_WhenRangesOverlap()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(36f, 140f, 400f),
                    new SelectionInputValues(36f, 140f, 400f)
                ])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            var coordinator = new FakeTileRunCoordinator(_ => { });
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = coordinator.Requests[0].Output.MeshParentSlotId.Should().Be(coordinator.Requests[1].Output.MeshParentSlotId);
            _ = coordinator.Requests[0].Output.MeshParentSlotId.Should().BeNull();
            _ = coordinator.Requests[1].SelectionReference.Latitude.Should().Be(36d);
            _ = coordinator.Requests[1].SelectionReference.Longitude.Should().Be(140d);
            _ = coordinator.Requests[1].PlacementReference.Latitude.Should().Be(35d);
            _ = coordinator.Requests[1].PlacementReference.Longitude.Should().Be(139d);
        }

        [Fact]
        public async Task RunAsync_ResetsPlacementReference_WhenRangesDoNotOverlap()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(2000f, 2000f, 400f),
                    new SelectionInputValues(2000f, 2000f, 400f)
                ])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            var coordinator = new FakeTileRunCoordinator(_ => { });
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = session.RemovedSlotIds.Should().BeEmpty();
            _ = coordinator.Requests[0].Output.MeshParentSlotId.Should().BeNull();
            _ = coordinator.Requests[1].Output.MeshParentSlotId.Should().BeNull();
            _ = coordinator.Requests[1].PlacementReference.Latitude.Should().Be(2000d);
            _ = coordinator.Requests[1].PlacementReference.Longitude.Should().Be(2000d);
        }

        [Fact]
        public async Task RunAsync_RangeChange_KeepsRetainedTilesButDropsCheckpoint()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    new SelectionInputValues(35f, 139f, 200f),
                    new SelectionInputValues(35f, 139f, 200f),
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35f, 139f, 400f)
                ])
            };
            string partialStableId = "partial";
            var partialVisibleTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [partialStableId] = new(partialStableId, "partial-tile", null, [], ["slot_partial"], "Google; Airbus")
            };
            var checkpoint = new InteractiveRunCheckpoint(new Dictionary<string, Tileset>(StringComparer.OrdinalIgnoreCase));
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            var coordinator = new FakeTileRunCoordinator(
                onRunStarted: _ => { },
                interactiveHandler: (callIndex, _, interactive, _) => Task.FromResult(
                    callIndex == 1
                        ? new InteractiveTileRunResult(
                            new RunSummary(1, 1, 1, 0),
                            partialVisibleTiles,
                            new HashSet<string>([partialStableId], StringComparer.Ordinal),
                            checkpoint)
                        : new InteractiveTileRunResult(
                            new RunSummary(1, 1, 1, 0),
                            new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal),
                            new HashSet<string>(interactive.RetainedTiles.Keys, StringComparer.Ordinal),
                            interactive.Checkpoint)));
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs[1].Should().ContainKey(partialStableId);
            _ = coordinator.CheckpointInputs[1].Should().BeNull();
            _ = session.RemovedSlotIds.Should().BeEmpty();
            _ = coordinator.Requests[1].PlacementReference.Latitude.Should().Be(35d);
            _ = coordinator.Requests[1].PlacementReference.Longitude.Should().Be(139d);
        }

        [Fact]
        public async Task RunAsync_CancellationDoesNotRemoveAnySlots()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>([new SelectionInputValues(35f, 139f, 400f), new SelectionInputValues(35f, 139f, 400f)])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 3 };
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = session.RemovedSlotIds.Should().BeEmpty();
        }

        [Fact]
        public async Task RunAsync_DisconnectsEvenWhenCallerTokenIsCanceled()
        {
            var session = new FakeSession
            {
                ThrowOnCanceledDisconnect = true
            };
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>([new SelectionInputValues(35f, 139f, 400f), new SelectionInputValues(35f, 139f, 400f)])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 3 };
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = session.DisconnectCalls.Should().Be(1);
            _ = session.DisconnectCancellationTokens.Should().ContainSingle()
                .Which.Should().BeFalse();
        }

        [Fact]
        public async Task RunAsync_SupersededOverlapRun_CarriesPartialVisibleTilesIntoNextRun()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(36f, 140f, 400f),
                    new SelectionInputValues(36f, 140f, 400f)
                ])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            string partialStableId = "partial";
            var partialVisibleTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [partialStableId] = new(partialStableId, "partial-tile", null, [], ["slot_partial"], "Google; Airbus")
            };
            var coordinator = new FakeTileRunCoordinator(
                onRunStarted: callIndex =>
                {
                    if (callIndex >= 2)
                    {
                        clock.RequestCancellation();
                    }
                },
                interactiveHandler: async (callIndex, _, interactive, cancellationToken) =>
                {
                    if (callIndex == 1)
                    {
                        await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                        return new InteractiveTileRunResult(
                            new RunSummary(1, 1, 1, 0),
                            partialVisibleTiles,
                            new HashSet<string>([partialStableId], StringComparer.Ordinal),
                            new InteractiveRunCheckpoint(
                                new Dictionary<string, Tileset>(StringComparer.OrdinalIgnoreCase)));
                    }

                    return new InteractiveTileRunResult(
                        new RunSummary(1, 1, 1, 0),
                        new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal),
                        new HashSet<string>(interactive.RetainedTiles.Keys, StringComparer.Ordinal),
                        null);
                });
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs[1].Should().ContainKey(partialStableId);
            _ = coordinator.CheckpointInputs[1].Should().NotBeNull();
        }

        [Fact]
        public async Task RunAsync_SupersedesActiveRun_WithoutWaitingForThrottleWindow()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    new SelectionInputValues(35f, 139f, 200f),
                    new SelectionInputValues(35f, 139f, 200f),
                    new SelectionInputValues(35.1f, 139.1f, 200f),
                    new SelectionInputValues(35.1f, 139.1f, 200f)
                ])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            var coordinator = new FakeTileRunCoordinator(
                onRunStarted: _ => { },
                interactiveHandler: async (callIndex, _, interactive, cancellationToken) =>
                {
                    if (callIndex == 1)
                    {
                        await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        clock.RequestCancellation();
                    }

                    return new InteractiveTileRunResult(
                        new RunSummary(1, 1, 1, 0),
                        new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal),
                        new HashSet<string>(interactive.RetainedTiles.Keys, StringComparer.Ordinal),
                        interactive.Checkpoint);
                });
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);
            var options = new InteractiveRunRequest(
                "127.0.0.1",
                12000,
                0d,
                new TraversalOptions(500d, 16, 16, 40d),
                false,
                string.Empty,
                false,
                new WatchOptions(
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMinutes(1)));

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(options, cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
        }

        [Fact]
        public async Task RunAsync_DropsRetainedTilesAndCheckpoint_WhenRangesDoNotOverlap()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(2000f, 2000f, 400f),
                    new SelectionInputValues(2000f, 2000f, 400f)
                ])
            };
            string partialStableId = "partial";
            var partialVisibleTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [partialStableId] = new(partialStableId, "partial-tile", null, [], ["slot_partial"], "Google; Airbus")
            };
            var checkpoint = new InteractiveRunCheckpoint(new Dictionary<string, Tileset>(StringComparer.OrdinalIgnoreCase));
            var coordinator = new FakeTileRunCoordinator(
                onRunStarted: _ => { },
                interactiveHandler: (callIndex, _, interactive, _) => Task.FromResult(
                    callIndex == 1
                        ? new InteractiveTileRunResult(
                            new RunSummary(1, 1, 1, 0),
                            partialVisibleTiles,
                            new HashSet<string>([partialStableId], StringComparer.Ordinal),
                            checkpoint)
                        : new InteractiveTileRunResult(
                            new RunSummary(1, 1, 1, 0),
                            new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal),
                            new HashSet<string>(interactive.RetainedTiles.Keys, StringComparer.Ordinal),
                            interactive.Checkpoint)));
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs[1].Should().BeEmpty();
            _ = coordinator.CheckpointInputs[1].Should().BeNull();
            _ = session.RemovedSlotIds.Should().Contain("slot_partial");
        }

        [Fact]
        public async Task RunAsync_KeepsRetainedTilesButDropsCheckpoint_WhenRangeChangesAtSameLocation()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35f, 139f, 800f),
                    new SelectionInputValues(35f, 139f, 800f)
                ])
            };
            string partialStableId = "partial";
            var partialVisibleTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [partialStableId] = new(partialStableId, "partial-tile", null, [], ["slot_partial"], "Google; Airbus")
            };
            var checkpoint = new InteractiveRunCheckpoint(new Dictionary<string, Tileset>(StringComparer.OrdinalIgnoreCase));
            var coordinator = new FakeTileRunCoordinator(
                onRunStarted: _ => { },
                interactiveHandler: (callIndex, _, interactive, _) => Task.FromResult(
                    callIndex == 1
                        ? new InteractiveTileRunResult(
                            new RunSummary(1, 1, 1, 0),
                            partialVisibleTiles,
                            new HashSet<string>([partialStableId], StringComparer.Ordinal),
                            checkpoint)
                        : new InteractiveTileRunResult(
                            new RunSummary(1, 1, 1, 0),
                            new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal),
                            new HashSet<string>(interactive.RetainedTiles.Keys, StringComparer.Ordinal),
                            interactive.Checkpoint)));
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs[1].Should().ContainKey(partialStableId);
            _ = coordinator.CheckpointInputs[1].Should().BeNull();
            _ = coordinator.Requests[0].PlacementReference.Latitude.Should().Be(35d);
            _ = coordinator.Requests[0].PlacementReference.Longitude.Should().Be(139d);
            _ = coordinator.Requests[1].PlacementReference.Latitude.Should().Be(35d);
            _ = coordinator.Requests[1].PlacementReference.Longitude.Should().Be(139d);
            _ = session.RemovedSlotIds.Should().BeEmpty();
        }

        [Fact]
        public async Task RunAsync_IgnoresSearchWithoutApiKey()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SearchValues = new Queue<string?>(["Shibuya", "Shibuya"]),
                SelectionInputValues = new Queue<SelectionInputValues?>([null, null])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 3 };
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var supervisor = CreateSupervisor(coordinator, session, watchStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = watchStore.UpdatedCoordinates.Should().BeEmpty();
            _ = coordinator.Requests.Should().BeEmpty();
        }

        [Fact]
        public async Task RunAsync_WaitsForWatchToReflectResolvedSearchCoordinates_BeforeStartingRun()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SearchValues = new Queue<string?>(["Asakusa", "Asakusa", "Asakusa", "Asakusa"]),
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35f, 139f, 400f),
                    new SelectionInputValues(35.7147651f, 139.7966553f, 400f),
                    new SelectionInputValues(35.7147651f, 139.7966553f, 400f)
                ])
            };
            var clock = new FakeClock();
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var searchResolver = new FakeSearchResolver(new LocationSearchResult("Asakusa", 35.7147651d, 139.7966553d));
            var supervisor = CreateSupervisor(coordinator, session, watchStore, searchResolver, clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: "key"), cts.Token);

            _ = watchStore.UpdatedCoordinates.Should().ContainSingle()
                .Which.Should().Be((35.7147651d, 139.7966553d));
            _ = coordinator.Requests.Should().ContainSingle();
            _ = coordinator.Requests[0].SelectionReference.Latitude.Should().BeApproximately(35.7147651d, 1e-5);
            _ = coordinator.Requests[0].SelectionReference.Longitude.Should().BeApproximately(139.7966553d, 1e-5);
            _ = coordinator.Requests[0].PlacementReference.Latitude.Should().BeApproximately(35.7147651d, 1e-5);
            _ = coordinator.Requests[0].PlacementReference.Longitude.Should().BeApproximately(139.7966553d, 1e-5);
            _ = coordinator.Requests[0].Traversal.RangeM.Should().Be(400d);
        }

        [Fact]
        public async Task RunAsync_StartsInitialRun_AfterSearchReflection_WhenValuesWereAlreadySet()
        {
            var session = new FakeSession();
            var reflectedValues = new SelectionInputValues(35.7147651f, 139.7966553f, 400f);
            var watchStore = new FakeInteractiveInputStore
            {
                SearchValues = new Queue<string?>(["Asakusa", "Asakusa", "Asakusa", "Asakusa"]),
                SelectionInputValues = new Queue<SelectionInputValues?>(
                [
                    reflectedValues,
                    reflectedValues,
                    reflectedValues,
                    reflectedValues
                ])
            };
            var clock = new FakeClock();
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var searchResolver = new FakeSearchResolver(new LocationSearchResult("Asakusa", 35.7147651d, 139.7966553d));
            var supervisor = CreateSupervisor(coordinator, session, watchStore, searchResolver, clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: "key"), cts.Token);

            _ = watchStore.UpdatedCoordinates.Should().ContainSingle()
                .Which.Should().Be((35.7147651d, 139.7966553d));
            _ = coordinator.Requests.Should().ContainSingle();
            _ = coordinator.Requests[0].SelectionReference.Latitude.Should().BeApproximately(35.7147651d, 1e-5);
            _ = coordinator.Requests[0].SelectionReference.Longitude.Should().BeApproximately(139.7966553d, 1e-5);
            _ = coordinator.Requests[0].Traversal.RangeM.Should().Be(400d);
        }

        [Fact]
        public async Task RunAsync_Throws_WhenWatchSearchReadDetectsDisconnectedSession()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SearchReadException = new ResoniteLinkDisconnectedException()
            };
            var supervisor = CreateSupervisor(
                new FakeTileRunCoordinator(static _ => { }),
                session,
                watchStore,
                new FakeSearchResolver(),
                new FakeClock());

            Func<Task> act = () => supervisor.RunAsync(CreateRequest(apiKey: string.Empty), CancellationToken.None);

            _ = await act.Should().ThrowAsync<ResoniteLinkDisconnectedException>();
            _ = session.DisconnectCalls.Should().Be(1);
        }

        [Fact]
        public async Task RunAsync_Disconnects_WhenInputBindingInitializationFailsAfterConnect()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                CreateBindingException = new InvalidOperationException("binding failed")
            };
            var supervisor = CreateSupervisor(
                new FakeTileRunCoordinator(static _ => { }),
                session,
                watchStore,
                new FakeSearchResolver(),
                new FakeClock());

            Func<Task> act = () => supervisor.RunAsync(CreateRequest(apiKey: string.Empty), CancellationToken.None);

            _ = await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("binding failed");
            _ = session.DisconnectCalls.Should().Be(1);
        }

        [Fact]
        public async Task RunAsync_PropagatesCancellation_WhenSearchResolutionIsCanceled()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SearchValues = new Queue<string?>(["Asakusa", "Asakusa"]),
                SelectionInputValues = new Queue<SelectionInputValues?>([null, null])
            };
            var cancellation = new OperationCanceledException("canceled");
            var supervisor = CreateSupervisor(
                new FakeTileRunCoordinator(static _ => { }),
                session,
                watchStore,
                new FakeSearchResolver(exception: cancellation),
                new FakeClock());

            Func<Task> act = () => supervisor.RunAsync(CreateRequest(apiKey: "key"), CancellationToken.None);

            _ = await act.Should().ThrowAsync<OperationCanceledException>();
            _ = session.DisconnectCalls.Should().Be(1);
        }

        [Fact]
        public async Task RunAsync_RetriesSameSearchText_AfterTransientFailure()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SearchValues = new Queue<string?>(
                [
                    "Asakusa", "Asakusa", "Asakusa", "Asakusa", "Asakusa", "Asakusa",
                    "Asakusa", "Asakusa", "Asakusa", "Asakusa"
                ]),
                SelectionInputValues = new Queue<SelectionInputValues?>([null, null, null, null, null, null, null, null, null, null])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 5 };
            var searchResolver = new FakeSearchResolver(
                outcomes:
                [
                    new HttpRequestException("transient"),
                    new LocationSearchResult("Asakusa", 35.7147651d, 139.7966553d)
                ]);
            var supervisor = CreateSupervisor(
                new FakeTileRunCoordinator(static _ => { }),
                session,
                watchStore,
                searchResolver,
                clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: "key"), cts.Token);

            _ = searchResolver.CallCount.Should().BeGreaterThanOrEqualTo(2);
            _ = watchStore.UpdatedCoordinates.Should().ContainSingle()
                .Which.Should().Be((35.7147651d, 139.7966553d));
        }

        [Fact]
        public async Task RunAsync_RequeuesSearch_WhenWatchCoordinateWritebackFails()
        {
            var session = new FakeSession();
            var watchStore = new FakeInteractiveInputStore
            {
                SearchValues = new Queue<string?>(["Asakusa", "Asakusa", "Asakusa"]),
                SelectionInputValues = new Queue<SelectionInputValues?>([null, null, null]),
                UpdateCoordinateFailures = new Queue<Exception?>([new HttpRequestException("writeback failed")])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 2 };
            var searchResolver = new FakeSearchResolver(new LocationSearchResult("Asakusa", 35.7147651d, 139.7966553d));
            var supervisor = CreateSupervisor(
                new FakeTileRunCoordinator(static _ => { }),
                session,
                watchStore,
                searchResolver,
                clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: "key"), cts.Token);

            _ = searchResolver.CallCount.Should().Be(1);
            _ = watchStore.UpdatedCoordinates.Should().BeEmpty();
        }

        private static InteractiveRunSupervisor CreateSupervisor(
            FakeTileRunCoordinator coordinator,
            FakeSession session,
            FakeInteractiveInputStore watchStore,
            FakeSearchResolver searchResolver,
            FakeClock clock)
        {
            return new InteractiveRunSupervisor(
                coordinator,
                session,
                watchStore,
                searchResolver,
                new FakeTransformer(),
                new FakeGeoReferenceResolver(),
                clock,
                new SelectionInputReader(watchStore, NullLogger<SelectionInputReader>.Instance),
                NullLoggerFactory.Instance,
                NullLogger<InteractiveRunSupervisor>.Instance);
        }

        private static InteractiveRunRequest CreateRequest(string apiKey)
        {
            return new InteractiveRunRequest(
                "127.0.0.1",
                12000,
                0d,
                new TraversalOptions(500d, 16, 16, 40d),
                false,
                apiKey,
                false,
                new WatchOptions(
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.Zero));
        }

        private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
            {
                _ = ((TaskCompletionSource)state!).TrySetResult();
            }, tcs);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await tcs.Task.ConfigureAwait(false);
        }

        private sealed class FakeGeoReferenceResolver : IGeoReferenceResolver
        {
            public GeoReference Resolve(double latitude, double longitude, double heightOffset)
            {
                return new GeoReference(latitude, longitude, 100d + heightOffset);
            }
        }

        private sealed class FakeTileRunCoordinator(
            Action<int> onRunStarted,
            Func<int, TileRunRequest, InteractiveRunInput, CancellationToken, Task<InteractiveTileRunResult>>? interactiveHandler = null) : ITileRunCoordinator
        {
            private readonly Action<int> _onRunStarted = onRunStarted;
            private readonly Func<int, TileRunRequest, InteractiveRunInput, CancellationToken, Task<InteractiveTileRunResult>>? _interactiveHandler = interactiveHandler;
            private int _interactiveRunCount;

            public List<TileRunRequest> Requests { get; } = [];
            public List<Dictionary<string, RetainedTileState>> RetainedTileInputs { get; } = [];
            public List<InteractiveRunCheckpoint?> CheckpointInputs { get; } = [];

            public Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                _onRunStarted(Requests.Count);
                return Task.FromResult(new RunSummary(1, 1, 1, 0));
            }

            public Task<InteractiveTileRunResult> RunInteractiveAsync(
                TileRunRequest request,
                InteractiveRunInput interactive,
                CancellationToken cancellationToken)
            {
                Requests.Add(request);
                RetainedTileInputs.Add(new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal));
                CheckpointInputs.Add(interactive.Checkpoint);
                int callIndex = ++_interactiveRunCount;
                _onRunStarted(callIndex);

                if (_interactiveHandler is not null)
                {
                    return _interactiveHandler(callIndex, request, interactive, cancellationToken);
                }

                return Task.FromResult(new InteractiveTileRunResult(
                    new RunSummary(1, 1, 1, 0),
                    new Dictionary<string, RetainedTileState>(interactive.RetainedTiles, StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal),
                    interactive.Checkpoint));
            }
        }

        private sealed class FakeSession : IResoniteSession
        {
            public List<string> RemovedSlotIds { get; } = [];
            public List<bool> DisconnectCancellationTokens { get; } = [];
            public int DisconnectCalls { get; private set; }
            public bool ThrowOnCanceledDisconnect { get; init; }

            public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
            {
                return Task.FromResult<string?>("mesh_slot");
            }

            public Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
            {
                RemovedSlotIds.Add(slotId);
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                DisconnectCalls++;
                DisconnectCancellationTokens.Add(cancellationToken.IsCancellationRequested);
                if (ThrowOnCanceledDisconnect && cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Disconnect should not receive a canceled token.");
                }

                return Task.CompletedTask;
            }
        }

        private sealed class FakeInteractiveInputStore : IInteractiveInputStore
        {
            public Queue<SelectionInputValues?> SelectionInputValues { get; init; } = new();
            public Queue<string?> SearchValues { get; init; } = new();
            public Queue<Exception?> UpdateCoordinateFailures { get; init; } = new();
            public List<(double Latitude, double Longitude)> UpdatedCoordinates { get; } = [];
            public Exception? CreateBindingException { get; init; }
            public Exception? SelectionInputValuesReadException { get; init; }
            public Exception? SearchReadException { get; init; }

            public Task<InteractiveInputBinding> CreateInteractiveInputBindingAsync(CancellationToken cancellationToken)
            {
                if (CreateBindingException is not null)
                {
                    throw CreateBindingException;
                }

                return Task.FromResult(new InteractiveInputBinding(
                    "lat",
                    "Value",
                    "lat_alias",
                    "Value",
                    "lon",
                    "Value",
                    "lon_alias",
                    "Value",
                    "range",
                    "Value",
                    "range_alias",
                    "Value",
                    "search",
                    "Value",
                    "search_alias",
                    "Value"));
            }

            public Task<SelectionInputValues?> ReadInteractiveInputValuesAsync(InteractiveInputBinding binding, CancellationToken cancellationToken)
            {
                if (SelectionInputValuesReadException is not null)
                {
                    throw SelectionInputValuesReadException;
                }

                return Task.FromResult(SelectionInputValues.Count > 0 ? SelectionInputValues.Dequeue() : null);
            }

            public Task<string?> ReadInteractiveInputSearchAsync(InteractiveInputBinding binding, CancellationToken cancellationToken)
            {
                if (SearchReadException is not null)
                {
                    throw SearchReadException;
                }

                return Task.FromResult(SearchValues.Count > 0 ? SearchValues.Dequeue() : null);
            }

            public Task UpdateInteractiveInputCoordinatesAsync(InteractiveInputBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
            {
                if (UpdateCoordinateFailures.Count > 0)
                {
                    Exception? failure = UpdateCoordinateFailures.Dequeue();
                    if (failure is not null)
                    {
                        return Task.FromException(failure);
                    }
                }

                UpdatedCoordinates.Add((latitude, longitude));
                return Task.CompletedTask;
            }
        }

        private sealed class FakeSearchResolver(
            LocationSearchResult? result = null,
            Exception? exception = null,
            IReadOnlyList<object?>? outcomes = null) : ISearchResolver
        {
            private readonly LocationSearchResult? _result = result ?? new LocationSearchResult("resolved", 35d, 139d);
            private readonly Exception? _exception = exception;
            private readonly Queue<object?>? _outcomes = outcomes is null ? null : new Queue<object?>(outcomes);

            public int CallCount { get; private set; }

            public Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken)
            {
                CallCount++;
                if (_outcomes is not null && _outcomes.Count > 0)
                {
                    object? outcome = _outcomes.Dequeue();
                    return outcome switch
                    {
                        Exception ex => Task.FromException<LocationSearchResult?>(ex),
                        LocationSearchResult location => Task.FromResult<LocationSearchResult?>(location),
                        null => Task.FromResult<LocationSearchResult?>(null),
                        _ => throw new InvalidOperationException("Unsupported fake search outcome.")
                    };
                }

                if (_exception is not null)
                {
                    return Task.FromException<LocationSearchResult?>(_exception);
                }

                return Task.FromResult(_result);
            }
        }

        private sealed class FakeTransformer : ICoordinateTransformer
        {
            public Vector3d GeographicToEcef(double latitudeDeg, double longitudeDeg, double height)
            {
                return new Vector3d(latitudeDeg, longitudeDeg, height);
            }

            public Vector3d EcefToEnu(Vector3d ecef, GeoReference reference)
            {
                return new Vector3d(ecef.X - reference.Latitude, ecef.Y - reference.Longitude, ecef.Z - reference.Height);
            }

            public Vector3d EnuToEun(Vector3d enu)
            {
                return enu;
            }
        }

        private sealed class FakeClock : IClock
        {
            private int _delayCalls;

            public CancellationTokenSource? CancellationSource { get; set; }
            public int CancelAfterDelayCalls { get; init; }

            public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;

            public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
            {
                _delayCalls++;
                UtcNow += delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(10) : delay;
                if (CancelAfterDelayCalls > 0 && _delayCalls >= CancelAfterDelayCalls)
                {
                    return CancellationSource?.CancelAsync() ?? Task.CompletedTask;
                }

                return Task.CompletedTask;
            }

            public void RequestCancellation()
            {
                _ = CancellationSource?.CancelAsync();
            }
        }
    }
}
