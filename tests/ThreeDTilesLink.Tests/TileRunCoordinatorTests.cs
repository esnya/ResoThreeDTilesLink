using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Mesh;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class TileRunCoordinatorTests
    {
        [Fact]
        public async Task Run_DryRun_ProcessesTilesWithoutSending()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") },
                    new Tile { Id = "1", ContentUri = new Uri("https://example.com/b.glb") }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: true), CancellationToken.None);

            _ = summary.CandidateTiles.Should().Be(2);
            _ = summary.ProcessedTiles.Should().Be(2);
            _ = summary.StreamedMeshes.Should().Be(2);
            _ = summary.FailedTiles.Should().Be(0);
            _ = client.ConnectCount.Should().Be(0);
            _ = client.SendCount.Should().Be(0);
            _ = client.RemoveCount.Should().Be(0);
            _ = client.ProgressUpdates.Should().BeEmpty();
        }

        [Fact]
        public async Task Run_SendFailureOnOneTile_ContinuesNextTile()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") },
                    new Tile { Id = "1", ContentUri = new Uri("https://example.com/b.glb") }
                ]
            });

            var client = new FakeResoniteSession(failFirstSend: true);
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false), CancellationToken.None);

            _ = summary.CandidateTiles.Should().Be(2);
            _ = summary.ProcessedTiles.Should().Be(1);
            _ = summary.StreamedMeshes.Should().Be(1);
            _ = summary.FailedTiles.Should().Be(1);
            _ = client.ConnectCount.Should().Be(1);
            _ = client.DisconnectCount.Should().Be(1);
            _ = client.SendCount.Should().Be(2);
            _ = client.ProgressUpdates.Should().NotBeEmpty();
            _ = client.ProgressUpdates[^1].Progress01.Should().Be(1f);
            _ = client.ProgressUpdates[^1].ProgressText.Should().Contain("Completed:");
        }

        [Fact]
        public async Task Run_ManageResoniteConnectionFalse_DoesNotConnectOrDisconnect()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false, manageConnection: false), CancellationToken.None);

            _ = summary.StreamedMeshes.Should().Be(1);
            _ = client.ConnectCount.Should().Be(0);
            _ = client.DisconnectCount.Should().Be(0);
            _ = client.ProgressUpdates.Should().NotBeEmpty();
        }

        [Fact]
        public async Task Run_NestedJsonTileset_TraversesAndStreamsLeafGlb()
        {
            var rootTileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/nested.json") }
                ]
            });

            var nestedTileset = new Tileset(new Tile
            {
                Id = "nestedRoot",
                Children =
                [
                    new Tile { Id = "leaf", ContentUri = new Uri("https://example.com/leaf.glb") }
                ]
            });

            TileRunCoordinator coordinator = CreateCoordinator(
                new FakeTilesSource(rootTileset, new Dictionary<string, Tileset>
                {
                    ["https://example.com/nested.json"] = nestedTileset
                }),
                new FakeResoniteSession());

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: true), CancellationToken.None);

            _ = summary.CandidateTiles.Should().Be(1);
            _ = summary.ProcessedTiles.Should().Be(2);
            _ = summary.StreamedMeshes.Should().Be(1);
            _ = summary.FailedTiles.Should().Be(0);
        }

        [Fact]
        public async Task Run_EnuToEun_ReversesTriangleWinding()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false, maxTiles: 8), CancellationToken.None);

            _ = summary.StreamedMeshes.Should().Be(1);
            _ = client.Payloads.Should().HaveCount(1);
            _ = client.Payloads[0].Indices.Should().Equal(0, 2, 1);
            _ = client.Payloads[0].Name.Should().NotContain("/");
        }

        [Fact]
        public async Task Run_SameGlbUriInMultipleTiles_StreamsAllInstances()
        {
            var sharedUri = new Uri("https://example.com/shared.glb");
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = sharedUri },
                    new Tile { Id = "1", ContentUri = sharedUri }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false, maxTiles: 8), CancellationToken.None);

            _ = summary.CandidateTiles.Should().Be(2);
            _ = summary.ProcessedTiles.Should().Be(2);
            _ = summary.StreamedMeshes.Should().Be(2);
            _ = summary.FailedTiles.Should().Be(0);
            _ = client.Payloads.Should().HaveCount(2);
        }

        [Fact]
        public async Task Run_StreamsCoarseBeforeNestedFine_WhenFineNotYetDiscovered()
        {
            var rootTileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "coarse", ContentUri = new Uri("https://example.com/coarse.glb") },
                    new Tile { Id = "j", ContentUri = new Uri("https://example.com/nested.json") }
                ]
            });

            var nestedTileset = new Tileset(new Tile
            {
                Id = "nestedRoot",
                Children =
                [
                    new Tile { Id = "leaf", ContentUri = new Uri("https://example.com/fine.glb") }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(
                new FakeTilesSource(rootTileset, new Dictionary<string, Tileset>
                {
                    ["https://example.com/nested.json"] = nestedTileset
                }),
                client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false), CancellationToken.None);

            _ = summary.StreamedMeshes.Should().Be(2);
            _ = client.Payloads.Should().HaveCount(2);
            _ = client.Payloads[0].Name.Should().Contain("tile_coarse_");
        }

        [Fact]
        public async Task Run_RemovesParent_WhenDirectChildBranchCompletes()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "p",
                        ContentUri = new Uri("https://example.com/p.glb"),
                        Children =
                        [
                            new Tile
                            {
                                Id = "c",
                                ContentUri = new Uri("https://example.com/c.glb")
                            }
                        ]
                    }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false), CancellationToken.None);

            _ = summary.FailedTiles.Should().Be(0);
            _ = client.RemovedSlotIds.Should().Contain(id => id.Contains("tile_p_m", StringComparison.Ordinal));
            _ = client.RemovedSlotIds.Should().NotContain(id => id.Contains("tile_c_m", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Run_RemovesParent_WhenJsonRelayBranchCompletes()
        {
            var rootTileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "p",
                        ContentUri = new Uri("https://example.com/p.glb"),
                        Children =
                        [
                            new Tile
                            {
                                Id = "j",
                                ContentUri = new Uri("https://example.com/nested.json")
                            }
                        ]
                    }
                ]
            });

            var nestedTileset = new Tileset(new Tile
            {
                Id = "nestedRoot",
                Children =
                [
                    new Tile { Id = "leaf", ContentUri = new Uri("https://example.com/leaf.glb") }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(
                new FakeTilesSource(rootTileset, new Dictionary<string, Tileset>
                {
                    ["https://example.com/nested.json"] = nestedTileset
                }),
                client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false), CancellationToken.None);

            _ = summary.FailedTiles.Should().Be(0);
            _ = client.RemovedSlotIds.Should().Contain(id => id.Contains("tile_p_m", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Run_ChildFailureStillCompletesBranch_AndRemovesParent()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "p",
                        ContentUri = new Uri("https://example.com/p.glb"),
                        Children =
                        [
                            new Tile
                            {
                                Id = "c",
                                ContentUri = new Uri("https://example.com/c.glb")
                            }
                        ]
                    }
                ]
            });

            var client = new FakeResoniteSession(failOnNameContains: "tile_c_m");
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false), CancellationToken.None);

            _ = summary.FailedTiles.Should().BeGreaterThanOrEqualTo(1);
            _ = client.RemovedSlotIds.Should().Contain(id => id.Contains("tile_p_m", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Run_SetsLicenseCredit_FromGlbAssetCopyright()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(
                new FakeTilesSource(
                    tileset,
                    tileContentByUri: new Dictionary<string, byte[]>
                    {
                        ["https://example.com/a.glb"] = [1]
                    }),
                client,
                new FakeExtractor(new Dictionary<byte, string>
                {
                    [1] = "Google; Maxar Technologies"
                }));

            _ = await coordinator.RunAsync(CreateRequest(dryRun: false, maxTiles: 8), CancellationToken.None);

            _ = client.LicenseCredits.Should().ContainInOrder("Google Maps", "Google; Maxar Technologies");
        }

        [Fact]
        public async Task Run_UpdatesLicenseCredit_WhenNestedTilesetAddsAttribution()
        {
            var rootTileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/nested.json") }
                ]
            });

            var nestedTileset = new Tileset(
                new Tile
                {
                    Id = "nestedRoot",
                    Children =
                    [
                        new Tile { Id = "leaf", ContentUri = new Uri("https://example.com/leaf.glb") }
                    ]
                });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(
                new FakeTilesSource(
                    rootTileset,
                    new Dictionary<string, Tileset>
                    {
                        ["https://example.com/nested.json"] = nestedTileset
                    },
                    new Dictionary<string, byte[]>
                    {
                        ["https://example.com/leaf.glb"] = [2]
                    }),
                client,
                new FakeExtractor(new Dictionary<byte, string>
                {
                    [2] = "Google; Airbus"
                }));

            _ = await coordinator.RunAsync(CreateRequest(dryRun: false), CancellationToken.None);

            _ = client.LicenseCredits.Should().ContainInOrder("Google Maps", "Google; Airbus");
        }

        [Fact]
        public async Task Run_RemovingParentTile_AlsoRemovesParentAttribution()
        {
            var rootTileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "p",
                        ContentUri = new Uri("https://example.com/p.glb"),
                        Children =
                        [
                            new Tile
                            {
                                Id = "j",
                                ContentUri = new Uri("https://example.com/nested.json")
                            }
                        ]
                    }
                ]
            });

            var nestedTileset = new Tileset(
                new Tile
                {
                    Id = "nestedRoot",
                    Children =
                    [
                        new Tile { Id = "leaf", ContentUri = new Uri("https://example.com/leaf.glb") }
                    ]
                });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(
                new FakeTilesSource(
                    rootTileset,
                    new Dictionary<string, Tileset>
                    {
                        ["https://example.com/nested.json"] = nestedTileset
                    },
                    new Dictionary<string, byte[]>
                    {
                        ["https://example.com/p.glb"] = [1],
                        ["https://example.com/leaf.glb"] = [2]
                    }),
                client,
                new FakeExtractor(new Dictionary<byte, string>
                {
                    [1] = "Google; RootProvider",
                    [2] = "Google; NestedProvider"
                }));

            _ = await coordinator.RunAsync(CreateRequest(dryRun: false), CancellationToken.None);

            _ = client.LicenseCredits.Should().ContainInOrder(
                "Google Maps",
                "Google; RootProvider",
                "Google; RootProvider; NestedProvider",
                "Google; NestedProvider");
        }

        [Fact]
        public async Task Run_AttributionParsing_PreservesCommaWithinSource_AndSeparatesBySemicolon()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/sample.glb") }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(
                new FakeTilesSource(
                    tileset,
                    tileContentByUri: new Dictionary<string, byte[]>
                    {
                        ["https://example.com/sample.glb"] = [3]
                    }),
                client,
                new FakeExtractor(new Dictionary<byte, string>
                {
                    [3] = "Data SIO, NOAA, U.S. Navy, NGA, GEBCO;Landsat / Copernicus"
                }));

            _ = await coordinator.RunAsync(CreateRequest(dryRun: false, maxTiles: 8), CancellationToken.None);

            _ = client.LicenseCredits.Should().ContainInOrder(
                "Google Maps",
                "Data SIO, NOAA, U.S. Navy, NGA, GEBCO; Landsat / Copernicus");
        }

        [Fact]
        public async Task Run_Bootstrap_DefersCoarseGlb_UntilStreamableChildDiscovered()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "p",
                        ContentUri = new Uri("https://example.com/p.glb"),
                        BoundingVolume = CreateBox(0d, 0d, 0d, 1200d),
                        Children =
                        [
                            new Tile
                            {
                                Id = "c",
                                ContentUri = new Uri("https://example.com/c.glb"),
                                BoundingVolume = CreateBox(0d, 0d, 0d, 120d)
                            }
                        ]
                    }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false, maxDepth: 16, bootstrapRangeMultiplier: 0.5d), CancellationToken.None);

            _ = summary.StreamedMeshes.Should().Be(1);
            _ = client.Payloads.Should().HaveCount(1);
            _ = client.Payloads[0].Name.Should().Contain("tile_c_");
            _ = client.Payloads[0].Name.Should().NotContain("tile_p_");
        }

        [Fact]
        public async Task Run_DeferredCoarseTile_FallbackStreamsParent_WhenNoChildBranchSelected()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "p",
                        ContentUri = new Uri("https://example.com/p.glb"),
                        BoundingVolume = CreateBox(0d, 0d, 0d, 1200d),
                        Children =
                        [
                            new Tile
                            {
                                Id = "c-out",
                                ContentUri = new Uri("https://example.com/c-out.glb"),
                                BoundingVolume = CreateBox(5000d, 5000d, 0d, 120d)
                            }
                        ]
                    }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false, maxDepth: 16, bootstrapRangeMultiplier: 0.5d), CancellationToken.None);

            _ = summary.StreamedMeshes.Should().Be(1);
            _ = client.Payloads.Should().HaveCount(1);
            _ = client.Payloads[0].Name.Should().Contain("tile_p_");
        }

        private static TileRunCoordinator CreateCoordinator(
            ITilesSource tilesSource,
            FakeResoniteSession session,
            IGlbMeshExtractor? extractor = null)
        {
            var transformer = new PassThroughTransformer();
            return new TileRunCoordinator(
                tilesSource,
                new TraversalPlanner(new TileSelector(transformer), NullLogger<TraversalPlanner>.Instance),
                new TileContentProcessor(tilesSource, extractor ?? new FakeExtractor()),
                new MeshPlacementService(transformer),
                session,
                new FakeGoogleAccessTokenProvider(),
                NullLogger<TileRunCoordinator>.Instance);
        }

        private static TileRunRequest CreateRequest(
            bool dryRun,
            int maxTiles = 16,
            int maxDepth = 8,
            double bootstrapRangeMultiplier = 4d,
            bool manageConnection = true)
        {
            return new TileRunRequest(
                new GeoReference(0d, 0d, 0d),
                new TraversalOptions(500d, maxTiles, maxDepth, 40d, bootstrapRangeMultiplier),
                new ResoniteOutputOptions("127.0.0.1", 12345, dryRun, manageConnection),
                "k");
        }

        private static BoundingVolume CreateBox(double cx, double cy, double cz, double halfExtent)
        {
            return new BoundingVolume
            {
                Box =
                [
                    cx, cy, cz,
                    halfExtent, 0d, 0d,
                    0d, halfExtent, 0d,
                    0d, 0d, halfExtent
                ]
            };
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

        private sealed class FakeTilesSource(
            Tileset tileset,
            IReadOnlyDictionary<string, Tileset>? nestedTilesets = null,
            IReadOnlyDictionary<string, byte[]>? tileContentByUri = null) : ITilesSource
        {
            private readonly Tileset _tileset = tileset;
            private readonly IReadOnlyDictionary<string, Tileset> _nestedTilesets = nestedTilesets ?? new Dictionary<string, Tileset>();
            private readonly IReadOnlyDictionary<string, byte[]> _tileContentByUri = tileContentByUri ?? new Dictionary<string, byte[]>();

            public Task<Tileset> FetchRootTilesetAsync(GoogleTilesAuth auth, CancellationToken cancellationToken)
            {
                return Task.FromResult(_tileset);
            }

            public Task<FetchedNodeContent> FetchNodeContentAsync(Uri contentUri, GoogleTilesAuth auth, CancellationToken cancellationToken)
            {
                return Task.FromResult<FetchedNodeContent>(
                    TileContentClassifier.Classify(contentUri) switch
                    {
                        TileContentKind.Json => _nestedTilesets.TryGetValue(contentUri.AbsoluteUri, out Tileset? nested)
                            ? new NestedTilesetFetchedContent(nested)
                            : throw new InvalidOperationException($"Unknown nested tileset URI: {contentUri}"),
                        TileContentKind.Glb => new GlbFetchedContent(
                            _tileContentByUri.TryGetValue(contentUri.AbsoluteUri, out byte[]? content)
                                ? content
                                : [1, 2, 3, 4]),
                        _ => new UnsupportedFetchedContent()
                    });
            }
        }

        private sealed class FakeExtractor(IReadOnlyDictionary<byte, string>? attributionByMarker = null) : IGlbMeshExtractor
        {
            private readonly IReadOnlyDictionary<byte, string> _attributionByMarker = attributionByMarker ?? new Dictionary<byte, string>();

            public GlbExtractResult Extract(byte[] glbBytes)
            {
                byte marker = glbBytes.Length > 0 ? glbBytes[0] : (byte)0;
                _ = _attributionByMarker.TryGetValue(marker, out string? attribution);
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
                attribution);
            }
        }

        private sealed class FakeResoniteSession(bool failFirstSend = false, string? failOnNameContains = null) : IResoniteSession
        {
            private readonly bool _failFirstSend = failFirstSend;
            private readonly string? _failOnNameContains = failOnNameContains;

            public int ConnectCount { get; private set; }
            public int DisconnectCount { get; private set; }
            public int SendCount { get; private set; }
            public int RemoveCount { get; private set; }
            public List<string> LicenseCredits { get; } = [];
            public List<PlacedMeshPayload> Payloads { get; } = [];
            public List<string> RemovedSlotIds { get; } = [];
            public List<(string? ParentSlotId, float Progress01, string ProgressText)> ProgressUpdates { get; } = [];

            public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
            {
                ConnectCount++;
                return Task.CompletedTask;
            }

            public Task<string> CreateSessionChildSlotAsync(string name, CancellationToken cancellationToken)
            {
                return Task.FromResult($"session_{name}");
            }

            public Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
            {
                LicenseCredits.Add(creditString);
                return Task.CompletedTask;
            }

            public Task SetProgressAsync(string? parentSlotId, float progress01, string progressText, CancellationToken cancellationToken)
            {
                ProgressUpdates.Add((parentSlotId, progress01, progressText));
                return Task.CompletedTask;
            }

            public Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
            {
                SendCount++;
                Payloads.Add(payload);
                return (_failFirstSend && SendCount == 1) ||
                    (!string.IsNullOrWhiteSpace(_failOnNameContains) &&
                     payload.Name.Contains(_failOnNameContains, StringComparison.Ordinal))
                    ? throw new InvalidOperationException("synthetic send failure")
                    : Task.FromResult<string?>($"slot_{SendCount}_{payload.Name}");
            }

            public Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
            {
                RemoveCount++;
                RemovedSlotIds.Add(slotId);
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                DisconnectCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeGoogleAccessTokenProvider : IGoogleAccessTokenProvider
        {
            public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult("token");
            }
        }
    }
}
