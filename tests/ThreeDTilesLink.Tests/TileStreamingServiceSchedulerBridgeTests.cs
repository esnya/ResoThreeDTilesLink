using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class TileStreamingServiceSchedulerBridgeTests
    {
        [Fact]
        public async Task Run_ExecutesSchedulerWorkItems_AndFeedsBackResults()
        {
            TileSelectionResult jsonTile = new(
                "j",
                new Uri("https://example.com/nested.json"),
                Matrix4x4d.Identity,
                0,
                null,
                TileContentKind.Json,
                false,
                10d,
                []);

            TileSelectionResult glbTile = new(
                "g",
                new Uri("https://example.com/g.glb"),
                Matrix4x4d.Identity,
                0,
                null,
                TileContentKind.Glb,
                false,
                10d,
                []);

            var scheduler = new FakeScheduler([
                new ProcessNodeContentWorkItem(jsonTile),
                new ProcessNodeContentWorkItem(glbTile),
                new RemoveParentTileSlotsWorkItem("state_p", "p", ["slot_to_remove"]),
                new UpdateLicenseCreditWorkItem("Google; TestProvider")
            ]);

            var service = new TileStreamingService(
                new BridgeFetcher(),
                scheduler,
                new BridgeExtractor(),
                new PassThroughTransformer(),
                new BridgeResoniteClient(),
                new BridgeTokenProvider(),
                NullLogger<TileStreamingService>.Instance);

            RunSummary summary = await service.RunAsync(
                new StreamerOptions(new GeoReference(0d, 0d, 0d), 500d, "127.0.0.1", 12000, 8, 8, 40d, false, "k"),
                CancellationToken.None);

            _ = summary.Should().Be(new RunSummary(9, 8, 7, 6));
            _ = scheduler.Results.Should().HaveCount(4);
            ProcessNodeContentWorkResult nested = scheduler.Results[0].Should().BeOfType<ProcessNodeContentWorkResult>().Subject;
            _ = nested.Outcome.Should().BeOfType<NestedTilesetContentOutcome>();
            ProcessNodeContentWorkResult streamed = scheduler.Results[1].Should().BeOfType<ProcessNodeContentWorkResult>().Subject;
            _ = streamed.Outcome.Should().BeOfType<StreamedRenderableContentOutcome>();
            _ = scheduler.Results[2].Should().BeOfType<RemoveParentTileSlotsWorkResult>();
            _ = scheduler.Results[3].Should().BeOfType<UpdateLicenseCreditWorkResult>();
        }

        private sealed class FakeScheduler(IReadOnlyList<SchedulerWorkItem> items) : ITileStreamingScheduler
        {
            private readonly Queue<SchedulerWorkItem> _items = new(items);

            public List<SchedulerWorkResult> Results { get; } = [];

            public void Initialize(Tileset rootTileset, StreamerOptions options)
            {
            }

            public bool TryDequeueWorkItem(out SchedulerWorkItem? workItem)
            {
                if (_items.Count == 0)
                {
                    workItem = null;
                    return false;
                }

                workItem = _items.Dequeue();
                return true;
            }

            public void HandleResult(SchedulerWorkResult result)
            {
                Results.Add(result);
            }

            public RunSummary GetSummary()
            {
                return new RunSummary(9, 8, 7, 6);
            }
        }

        private sealed class BridgeFetcher : ITileContentFetcher
        {
            public Task<Tileset> FetchRootTilesetAsync(GoogleTilesAuth auth, CancellationToken cancellationToken)
            {
                return Task.FromResult(new Tileset(new Tile { Id = "root" }));
            }

            public Task<FetchedNodeContent> FetchNodeContentAsync(Uri contentUri, GoogleTilesAuth auth, CancellationToken cancellationToken)
            {
                return Task.FromResult<FetchedNodeContent>(
                    TileContentClassifier.Classify(contentUri) switch
                    {
                        TileContentKind.Json => new NestedTilesetFetchedContent(new Tileset(new Tile { Id = "nestedRoot" })),
                        TileContentKind.Glb => new GlbFetchedContent([42]),
                        _ => new UnsupportedFetchedContent()
                    });
            }
        }

        private sealed class BridgeExtractor : IGlbMeshExtractor
        {
            public GlbExtractResult Extract(byte[] glbBytes)
            {
                return new GlbExtractResult(
                [
                    new MeshData(
                        "m",
                        [new Vector3d(1d, 2d, 3d), new Vector3d(2d, 2d, 3d), new Vector3d(1d, 3d, 3d)],
                        [0, 1, 2],
                        [new Vector2d(0d, 0d), new Vector2d(1d, 0d), new Vector2d(0d, 1d)],
                        true,
                        Matrix4x4d.Identity,
                        null,
                        null)
                ],
                "Google; Bridge");
            }
        }

        private sealed class BridgeResoniteClient : IResoniteLinkClient
        {
            public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<string?> SendTileMeshAsync(TileMeshPayload payload, CancellationToken cancellationToken)
            {
                return Task.FromResult<string?>("slot_streamed");
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

        private sealed class BridgeTokenProvider : IGoogleAccessTokenProvider
        {
            public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult("token");
            }
        }

        private sealed class PassThroughTransformer : ICoordinateTransformer
        {
            public Vector3d GeographicToEcef(double latitudeDeg, double longitudeDeg, double heightM)
            {
                return new(latitudeDeg, longitudeDeg, heightM);
            }

            public Vector3d EcefToEnu(Vector3d ecef, GeoReference reference)
            {
                return ecef;
            }

            public Vector3d EnuToEun(Vector3d enu)
            {
                return new(enu.X, enu.Z, enu.Y);
            }
        }
    }
}
