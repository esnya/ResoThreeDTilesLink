using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.Tests
{
    public sealed class InteractiveRunSupervisorTests
    {
        [Fact]
        public async Task RunAsync_StartsRun_WhenProbeValuesChange()
        {
            var session = new FakeSession();
            var probeStore = new FakeProbeStore
            {
                ProbeValues = new Queue<ProbeValues?>([new ProbeValues(35f, 139f, 400f), new ProbeValues(35f, 139f, 400f)])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 3 };
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var supervisor = CreateSupervisor(coordinator, session, probeStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().ContainSingle();
            _ = session.CreatedRunSlots.Should().ContainSingle();
            _ = coordinator.Requests[0].Traversal.RangeM.Should().Be(400d);
            _ = coordinator.Requests[0].Output.ManageConnection.Should().BeFalse();
            _ = coordinator.Requests[0].Output.MeshParentSlotId.Should().Be(session.CreatedRunSlots[0]);
        }

        [Fact]
        public async Task RunAsync_ReusesSessionSlot_AndPlacementReference_WhenRangesOverlap()
        {
            var session = new FakeSession();
            var probeStore = new FakeProbeStore
            {
                ProbeValues = new Queue<ProbeValues?>(
                [
                    new ProbeValues(35f, 139f, 400f),
                    new ProbeValues(35f, 139f, 400f),
                    new ProbeValues(36f, 140f, 400f),
                    new ProbeValues(36f, 140f, 400f)
                ])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            var coordinator = new FakeTileRunCoordinator(_ => { });
            var supervisor = CreateSupervisor(coordinator, session, probeStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = session.CreatedRunSlots.Should().ContainSingle();
            _ = coordinator.Requests[0].Output.MeshParentSlotId.Should().Be(coordinator.Requests[1].Output.MeshParentSlotId);
            _ = coordinator.Requests[1].SelectionReference.Latitude.Should().Be(36d);
            _ = coordinator.Requests[1].SelectionReference.Longitude.Should().Be(140d);
            _ = coordinator.Requests[1].PlacementReference.Latitude.Should().Be(35d);
            _ = coordinator.Requests[1].PlacementReference.Longitude.Should().Be(139d);
        }

        [Fact]
        public async Task RunAsync_RecreatesSessionSlot_WhenRangesDoNotOverlap()
        {
            var session = new FakeSession();
            var probeStore = new FakeProbeStore
            {
                ProbeValues = new Queue<ProbeValues?>(
                [
                    new ProbeValues(35f, 139f, 400f),
                    new ProbeValues(35f, 139f, 400f),
                    new ProbeValues(2000f, 2000f, 400f),
                    new ProbeValues(2000f, 2000f, 400f)
                ])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 4 };
            var coordinator = new FakeTileRunCoordinator(_ => { });
            var supervisor = CreateSupervisor(coordinator, session, probeStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = session.CreatedRunSlots.Should().HaveCount(2);
            _ = coordinator.Requests[0].Output.MeshParentSlotId.Should().NotBe(coordinator.Requests[1].Output.MeshParentSlotId);
            _ = coordinator.Requests[1].PlacementReference.Latitude.Should().Be(2000d);
            _ = coordinator.Requests[1].PlacementReference.Longitude.Should().Be(2000d);
        }

        [Fact]
        public async Task RunAsync_SupersededOverlapRun_CarriesPartialVisibleTilesIntoNextRun()
        {
            var session = new FakeSession();
            var probeStore = new FakeProbeStore
            {
                ProbeValues = new Queue<ProbeValues?>(
                [
                    new ProbeValues(35f, 139f, 400f),
                    new ProbeValues(35f, 139f, 400f),
                    new ProbeValues(36f, 140f, 400f),
                    new ProbeValues(36f, 140f, 400f)
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
                interactiveHandler: async (callIndex, _, retainedTiles, cancellationToken) =>
                {
                    if (callIndex == 1)
                    {
                        await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                        return new InteractiveTileRunResult(
                            new RunSummary(1, 1, 1, 0),
                            partialVisibleTiles,
                            new HashSet<string>([partialStableId], StringComparer.Ordinal));
                    }

                    return new InteractiveTileRunResult(
                        new RunSummary(1, 1, 1, 0),
                        new Dictionary<string, RetainedTileState>(retainedTiles, StringComparer.Ordinal),
                        new HashSet<string>(retainedTiles.Keys, StringComparer.Ordinal));
                });
            var supervisor = CreateSupervisor(coordinator, session, probeStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = coordinator.Requests.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs.Should().HaveCount(2);
            _ = coordinator.RetainedTileInputs[1].Should().ContainKey(partialStableId);
        }

        [Fact]
        public async Task RunAsync_IgnoresSearchWithoutApiKey()
        {
            var session = new FakeSession();
            var probeStore = new FakeProbeStore
            {
                SearchValues = new Queue<string?>(["Shibuya", "Shibuya"]),
                ProbeValues = new Queue<ProbeValues?>([null, null])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 3 };
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var supervisor = CreateSupervisor(coordinator, session, probeStore, new FakeSearchResolver(), clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: string.Empty), cts.Token);

            _ = probeStore.UpdatedCoordinates.Should().BeEmpty();
            _ = coordinator.Requests.Should().BeEmpty();
        }

        [Fact]
        public async Task RunAsync_WaitsForProbeToReflectResolvedSearchCoordinates_BeforeStartingRun()
        {
            var session = new FakeSession();
            var probeStore = new FakeProbeStore
            {
                SearchValues = new Queue<string?>(["Asakusa", "Asakusa", "Asakusa", "Asakusa"]),
                ProbeValues = new Queue<ProbeValues?>(
                [
                    new ProbeValues(35f, 139f, 400f),
                    new ProbeValues(35f, 139f, 400f),
                    new ProbeValues(35.7147651f, 139.7966553f, 400f),
                    new ProbeValues(35.7147651f, 139.7966553f, 400f)
                ])
            };
            var clock = new FakeClock();
            var coordinator = new FakeTileRunCoordinator(_ => clock.RequestCancellation());
            var searchResolver = new FakeSearchResolver(new LocationSearchResult("Asakusa", 35.7147651d, 139.7966553d));
            var supervisor = CreateSupervisor(coordinator, session, probeStore, searchResolver, clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: "key"), cts.Token);

            _ = probeStore.UpdatedCoordinates.Should().ContainSingle()
                .Which.Should().Be((35.7147651d, 139.7966553d));
            _ = coordinator.Requests.Should().ContainSingle();
            _ = coordinator.Requests[0].SelectionReference.Latitude.Should().BeApproximately(35.7147651d, 1e-5);
            _ = coordinator.Requests[0].SelectionReference.Longitude.Should().BeApproximately(139.7966553d, 1e-5);
            _ = coordinator.Requests[0].PlacementReference.Latitude.Should().BeApproximately(35.7147651d, 1e-5);
            _ = coordinator.Requests[0].PlacementReference.Longitude.Should().BeApproximately(139.7966553d, 1e-5);
            _ = coordinator.Requests[0].Traversal.RangeM.Should().Be(400d);
        }

        [Fact]
        public async Task RunAsync_Throws_WhenProbeSearchReadDetectsDisconnectedSession()
        {
            var session = new FakeSession();
            var probeStore = new FakeProbeStore
            {
                SearchReadException = new InvalidOperationException("ResoniteLink is not connected.")
            };
            var supervisor = CreateSupervisor(
                new FakeTileRunCoordinator(static _ => { }),
                session,
                probeStore,
                new FakeSearchResolver(),
                new FakeClock());

            Func<Task> act = () => supervisor.RunAsync(CreateRequest(apiKey: string.Empty), CancellationToken.None);

            var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
            _ = assertion.WithMessage("ResoniteLink is not connected.");
            _ = session.DisconnectCalls.Should().Be(1);
        }

        private static InteractiveRunSupervisor CreateSupervisor(
            FakeTileRunCoordinator coordinator,
            FakeSession session,
            FakeProbeStore probeStore,
            FakeSearchResolver searchResolver,
            FakeClock clock)
        {
            return new InteractiveRunSupervisor(
                coordinator,
                session,
                probeStore,
                searchResolver,
                new FakeTransformer(),
                clock,
                new ProbeMonitor(probeStore, NullLogger<ProbeMonitor>.Instance),
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
                new ProbeWatchOptions(
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.Zero,
                    new ProbeConfiguration(
                        "Probe",
                        "World/ThreeDTilesLink.Latitude",
                        "World/ThreeDTilesLink.Longitude",
                        "World/ThreeDTilesLink.Range",
                        "World/ThreeDTilesLink.Search")));
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

        private sealed class FakeTileRunCoordinator(
            Action<int> onRunStarted,
            Func<int, TileRunRequest, IReadOnlyDictionary<string, RetainedTileState>, CancellationToken, Task<InteractiveTileRunResult>>? interactiveHandler = null) : ITileRunCoordinator
        {
            private readonly Action<int> _onRunStarted = onRunStarted;
            private readonly Func<int, TileRunRequest, IReadOnlyDictionary<string, RetainedTileState>, CancellationToken, Task<InteractiveTileRunResult>>? _interactiveHandler = interactiveHandler;
            private int _interactiveRunCount;

            public List<TileRunRequest> Requests { get; } = [];
            public List<Dictionary<string, RetainedTileState>> RetainedTileInputs { get; } = [];

            public Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                _onRunStarted(Requests.Count);
                return Task.FromResult(new RunSummary(1, 1, 1, 0));
            }

            public Task<InteractiveTileRunResult> RunInteractiveAsync(
                TileRunRequest request,
                IReadOnlyDictionary<string, RetainedTileState> retainedTiles,
                bool removeOutOfRangeTiles,
                CancellationToken cancellationToken)
            {
                Requests.Add(request);
                RetainedTileInputs.Add(new Dictionary<string, RetainedTileState>(retainedTiles, StringComparer.Ordinal));
                int callIndex = ++_interactiveRunCount;
                _onRunStarted(callIndex);

                if (_interactiveHandler is not null)
                {
                    return _interactiveHandler(callIndex, request, retainedTiles, cancellationToken);
                }

                return Task.FromResult(new InteractiveTileRunResult(
                    new RunSummary(1, 1, 1, 0),
                    new Dictionary<string, RetainedTileState>(retainedTiles, StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal)));
            }
        }

        private sealed class FakeSession : IResoniteSession
        {
            public List<string> CreatedRunSlots { get; } = [];
            public int DisconnectCalls { get; private set; }

            public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<string> CreateSessionChildSlotAsync(string name, CancellationToken cancellationToken)
            {
                string slotId = $"slot_{CreatedRunSlots.Count + 1}";
                CreatedRunSlots.Add(slotId);
                return Task.FromResult(slotId);
            }

            public Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task SetProgressAsync(string? parentSlotId, float progress01, string progressText, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
            {
                return Task.FromResult<string?>("mesh_slot");
            }

            public Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                DisconnectCalls++;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeProbeStore : IProbeStore
        {
            public Queue<ProbeValues?> ProbeValues { get; init; } = new();
            public Queue<string?> SearchValues { get; init; } = new();
            public List<(double Latitude, double Longitude)> UpdatedCoordinates { get; } = [];
            public Exception? ProbeValuesReadException { get; init; }
            public Exception? SearchReadException { get; init; }

            public Task<ProbeBinding> CreateProbeAsync(ProbeConfiguration configuration, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ProbeBinding("probe", false, "lat", "Value", "lon", "Value", "range", "Value", "search", "Value"));
            }

            public Task<ProbeValues?> ReadProbeValuesAsync(ProbeBinding binding, CancellationToken cancellationToken)
            {
                if (ProbeValuesReadException is not null)
                {
                    throw ProbeValuesReadException;
                }

                return Task.FromResult(ProbeValues.Count > 0 ? ProbeValues.Dequeue() : null);
            }

            public Task<string?> ReadProbeSearchAsync(ProbeBinding binding, CancellationToken cancellationToken)
            {
                if (SearchReadException is not null)
                {
                    throw SearchReadException;
                }

                return Task.FromResult(SearchValues.Count > 0 ? SearchValues.Dequeue() : null);
            }

            public Task UpdateProbeCoordinatesAsync(ProbeBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
            {
                UpdatedCoordinates.Add((latitude, longitude));
                return Task.CompletedTask;
            }
        }

        private sealed class FakeSearchResolver(LocationSearchResult? result = null) : ISearchResolver
        {
            private readonly LocationSearchResult? _result = result ?? new LocationSearchResult("resolved", 35d, 139d);

            public Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken)
            {
                return Task.FromResult(_result);
            }
        }

        private sealed class FakeTransformer : ICoordinateTransformer
        {
            public Vector3d GeographicToEcef(double latitudeDeg, double longitudeDeg, double heightM)
            {
                return new Vector3d(latitudeDeg, longitudeDeg, heightM);
            }

            public Vector3d EcefToEnu(Vector3d ecef, GeoReference reference)
            {
                return new Vector3d(ecef.X - reference.Latitude, ecef.Y - reference.Longitude, ecef.Z - reference.HeightM);
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
                    CancellationSource?.Cancel();
                }

                return Task.CompletedTask;
            }

            public void RequestCancellation()
            {
                CancellationSource?.Cancel();
            }
        }
    }
}
