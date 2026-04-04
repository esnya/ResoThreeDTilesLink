using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Google;
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
            var coordinator = new FakeTileRunCoordinator(() => clock.RequestCancellation());
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
        public async Task RunAsync_IgnoresSearchWithoutApiKey()
        {
            var session = new FakeSession();
            var probeStore = new FakeProbeStore
            {
                SearchValues = new Queue<string?>(["Shibuya", "Shibuya"]),
                ProbeValues = new Queue<ProbeValues?>([null, null])
            };
            var clock = new FakeClock { CancelAfterDelayCalls = 3 };
            var coordinator = new FakeTileRunCoordinator(() => clock.RequestCancellation());
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
            var coordinator = new FakeTileRunCoordinator(() => clock.RequestCancellation());
            var searchResolver = new FakeSearchResolver(new LocationSearchResult("Asakusa", 35.7147651d, 139.7966553d));
            var supervisor = CreateSupervisor(coordinator, session, probeStore, searchResolver, clock);

            using var cts = new CancellationTokenSource();
            clock.CancellationSource = cts;

            await supervisor.RunAsync(CreateRequest(apiKey: "key"), cts.Token);

            _ = probeStore.UpdatedCoordinates.Should().ContainSingle()
                .Which.Should().Be((35.7147651d, 139.7966553d));
            _ = coordinator.Requests.Should().ContainSingle();
            _ = coordinator.Requests[0].Reference.Latitude.Should().BeApproximately(35.7147651d, 1e-5);
            _ = coordinator.Requests[0].Reference.Longitude.Should().BeApproximately(139.7966553d, 1e-5);
            _ = coordinator.Requests[0].Traversal.RangeM.Should().Be(400d);
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

        private sealed class FakeTileRunCoordinator(Action onRunStarted) : ITileRunCoordinator
        {
            private readonly Action _onRunStarted = onRunStarted;

            public List<TileRunRequest> Requests { get; } = [];

            public Task<RunSummary> RunAsync(TileRunRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                _onRunStarted();
                return Task.FromResult(new RunSummary(1, 1, 1, 0));
            }
        }

        private sealed class FakeSession : IResoniteSession
        {
            public List<string> CreatedRunSlots { get; } = [];

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
                return Task.CompletedTask;
            }
        }

        private sealed class FakeProbeStore : IProbeStore
        {
            public Queue<ProbeValues?> ProbeValues { get; init; } = new();
            public Queue<string?> SearchValues { get; init; } = new();
            public List<(double Latitude, double Longitude)> UpdatedCoordinates { get; } = [];

            public Task<ProbeBinding> CreateProbeAsync(ProbeConfiguration configuration, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ProbeBinding("probe", false, "lat", "Value", "lon", "Value", "range", "Value", "search", "Value"));
            }

            public Task<ProbeValues?> ReadProbeValuesAsync(ProbeBinding binding, CancellationToken cancellationToken)
            {
                return Task.FromResult(ProbeValues.Count > 0 ? ProbeValues.Dequeue() : null);
            }

            public Task<string?> ReadProbeSearchAsync(ProbeBinding binding, CancellationToken cancellationToken)
            {
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
