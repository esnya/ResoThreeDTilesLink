using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;
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
            _ = summary.ProcessedTiles.Should().Be(3);
            _ = summary.StreamedMeshes.Should().Be(2);
            _ = summary.FailedTiles.Should().Be(1);
            _ = client.ConnectCount.Should().Be(1);
            _ = client.DisconnectCount.Should().Be(1);
            _ = client.SendCount.Should().Be(3);
            _ = client.ProgressUpdates.Should().NotBeEmpty();
            _ = client.ProgressUpdates.Should().Contain(update => update.ProgressText.StartsWith("Running:", StringComparison.Ordinal));
            _ = client.ProgressUpdates[^1].Progress01.Should().Be(1f);
            _ = client.ProgressUpdates[^1].ProgressText.Should().Contain("Completed:");
            _ = client.ProgressUpdates[^1].ProgressText.Should().Contain("failed=1");
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
        public async Task Run_SetsInitialFetchingProgress_BeforeTraversalWork()
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

            _ = await coordinator.RunAsync(CreateRequest(dryRun: false, manageConnection: false), CancellationToken.None);

            _ = client.ProgressUpdates.Should().NotBeEmpty();
            _ = client.ProgressUpdates[0].Progress01.Should().Be(0f);
            _ = client.ProgressUpdates[0].ProgressText.Should().Be("Fetching root tileset...");
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
        public async Task RunInteractive_RetainsExistingVisibleTile_WithoutRestreaming()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "near",
                        ContentUri = new Uri("https://example.com/near.glb"),
                        BoundingVolume = CreateBox(0d, 0d, 0d, 10d)
                    }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);
            var retainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("near")] = new(StableId("near"), "near", null, [], ["slot_existing"], "Google; Airbus")
            };

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(retainedTiles, RemoveOutOfRangeTiles: false),
                CancellationToken.None);

            _ = client.SendCount.Should().Be(0);
            _ = result.VisibleTiles.Should().ContainKey(StableId("near"));
            _ = result.VisibleTiles[StableId("near")].SlotIds.Should().ContainSingle().Which.Should().Be("slot_existing");
        }

        [Fact]
        public async Task RunInteractive_KeepOutOfRangeRetainedTiles_WhenCleanupDisabled()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "near",
                        ContentUri = new Uri("https://example.com/near.glb"),
                        BoundingVolume = CreateBox(0d, 0d, 0d, 10d)
                    },
                    new Tile
                    {
                        Id = "far",
                        ContentUri = new Uri("https://example.com/far.glb"),
                        BoundingVolume = CreateBox(900d, 0d, 0d, 10d)
                    }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);
            var retainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("far")] = new(StableId("far"), "far", null, [], ["slot_far"], "Google; Maxar")
            };

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(retainedTiles, RemoveOutOfRangeTiles: false),
                CancellationToken.None);

            _ = client.RemovedSlotIds.Should().NotContain("slot_far");
            _ = result.VisibleTiles.Should().ContainKey(StableId("near"));
            _ = result.VisibleTiles.Should().ContainKey(StableId("far"));
        }

        [Fact]
        public async Task RunInteractive_RemovesOutOfRangeRetainedTiles_WhenCleanupEnabled()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "near",
                        ContentUri = new Uri("https://example.com/near.glb"),
                        BoundingVolume = CreateBox(0d, 0d, 0d, 10d)
                    },
                    new Tile
                    {
                        Id = "far",
                        ContentUri = new Uri("https://example.com/far.glb"),
                        BoundingVolume = CreateBox(900d, 0d, 0d, 10d)
                    }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);
            var retainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("far")] = new(StableId("far"), "far", null, [], ["slot_far"], "Google; Maxar")
            };

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(retainedTiles, RemoveOutOfRangeTiles: true),
                CancellationToken.None);

            _ = client.RemovedSlotIds.Should().Contain("slot_far");
            _ = result.VisibleTiles.Should().ContainKey(StableId("near"));
            _ = result.VisibleTiles.Should().NotContainKey(StableId("far"));
        }

        [Fact]
        public async Task RunInteractive_CleanupEnabled_BackfillsAncestorWhenRetainedDescendantIsOutOfRange()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "coarse",
                        ContentUri = new Uri("https://example.com/coarse.glb"),
                        BoundingVolume = CreateBox(0d, 0d, 0d, 40d),
                        Children = [new Tile { Id = "placeholder-child" }]
                    }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);
            string coarseStableId = StableId("coarse");
            string childStableId = StableId("fine");
            var retainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [childStableId] = new(childStableId, "fine", coarseStableId, [coarseStableId], ["slot_fine"], "Google; Airbus")
            };

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(retainedTiles, RemoveOutOfRangeTiles: true),
                CancellationToken.None);

            _ = client.SendCount.Should().Be(1);
            _ = client.RemovedSlotIds.Should().Contain("slot_fine");
            _ = result.VisibleTiles.Should().ContainKey(coarseStableId);
            _ = result.VisibleTiles.Should().NotContainKey(childStableId);
        }

        [Fact]
        public async Task RunInteractive_KeepMode_DoesNotStreamParent_WhenRetainedChildAlreadyVisible()
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
                        BoundingVolume = CreateBox(0d, 0d, 0d, 80d),
                        Children =
                        [
                            new Tile
                            {
                                Id = "c",
                                ContentUri = new Uri("https://example.com/c.glb"),
                                BoundingVolume = CreateBox(80d, 0d, 0d, 10d)
                            }
                        ]
                    }
                ]
            });

            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);
            string parentStableId = StableId("p");
            string childStableId = StableId("c");
            var retainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [childStableId] = new(childStableId, "c", parentStableId, [parentStableId], ["slot_child"], "Google; Airbus")
            };

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(retainedTiles, RemoveOutOfRangeTiles: false),
                CancellationToken.None);

            _ = client.SendCount.Should().Be(0);
            _ = result.VisibleTiles.Should().ContainKey(childStableId);
            _ = result.VisibleTiles.Should().NotContainKey(parentStableId);
        }

        [Fact]
        public async Task RunInteractive_CancelDuringOverlapUpdate_PreservesStreamedAndRetainedTiles()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "near-a",
                        ContentUri = new Uri("https://example.com/near-a.glb"),
                        BoundingVolume = CreateBox(0d, 0d, 0d, 10d)
                    },
                    new Tile
                    {
                        Id = "near-b",
                        ContentUri = new Uri("https://example.com/near-b.glb"),
                        BoundingVolume = CreateBox(20d, 0d, 0d, 10d)
                    }
                ]
            });

            using var cts = new CancellationTokenSource();
            var client = new FakeResoniteSession(onStreamCompleted: (_, sendCount) =>
            {
                if (sendCount == 1)
                {
                    cts.Cancel();
                }
            });
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);
            string farStableId = StableId("far");
            var retainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [farStableId] = new(farStableId, "far", null, [], ["slot_far"], "Google; Maxar")
            };

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(retainedTiles, RemoveOutOfRangeTiles: true),
                cts.Token);

            _ = client.SendCount.Should().Be(1);
            _ = client.RemovedSlotIds.Should().BeEmpty();
            _ = result.VisibleTiles.Should().ContainKey(StableId("near-a"));
            _ = result.VisibleTiles.Should().ContainKey(farStableId);
            _ = result.VisibleTiles.Should().NotContainKey(StableId("near-b"));
        }

        [Fact]
        public async Task RunInteractive_CancelAfterSendStarts_DrainsSuccessfulWriterCompletion()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") }
                ]
            });

            using var cts = new CancellationTokenSource();
            var client = new FakeResoniteSession(
                streamDelay: TimeSpan.FromMilliseconds(50),
                ignoreCancellationDuringStream: true);
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            Task<InteractiveTileRunResult> runTask = coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal), RemoveOutOfRangeTiles: false),
                cts.Token);

            for (int i = 0; i < 50 && client.SendCount == 0; i++)
            {
                await Task.Delay(10);
            }

            cts.Cancel();
            InteractiveTileRunResult result = await runTask.ConfigureAwait(true);

            _ = client.SendCount.Should().Be(1);
            _ = result.VisibleTiles.Should().ContainKey(StableId("0"));
            _ = result.Summary.StreamedMeshes.Should().Be(1);
        }

        [Fact]
        public async Task RunInteractive_CancelAfterNestedFetchStarts_DrainsCheckpointCache()
        {
            var rootTileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "j", ContentUri = new Uri("https://example.com/nested.json") }
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

            using var cts = new CancellationTokenSource();
            var tilesSource = new FakeTilesSource(
                rootTileset,
                new Dictionary<string, Tileset>
                {
                    ["https://example.com/nested.json"] = nestedTileset
                },
                contentDelay: TimeSpan.FromMilliseconds(50),
                ignoreCancellationDuringContent: true);
            TileRunCoordinator coordinator = CreateCoordinator(tilesSource, new FakeResoniteSession());

            Task<InteractiveTileRunResult> runTask = coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: true, manageConnection: false),
                new InteractiveRunInput(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal), RemoveOutOfRangeTiles: false),
                cts.Token);

            for (int i = 0; i < 50 && tilesSource.ContentFetchCount == 0; i++)
            {
                await Task.Delay(10);
            }

            cts.Cancel();
            InteractiveTileRunResult result = await runTask.ConfigureAwait(true);

            _ = result.Checkpoint.Should().NotBeNull();
            _ = result.Checkpoint!.TilesetCache.Should().ContainKey("https://example.com/nested.json");
        }

        [Fact]
        public async Task Run_SuppressesParentPlaceholder_WhenDirectChildAlreadySelected()
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
            _ = client.Payloads.Should().OnlyContain(payload => !payload.Name.Contains("tile_p_m", StringComparison.Ordinal));
            _ = client.RemovedSlotIds.Should().NotContain(id => id.Contains("tile_c_m", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Run_SuppressesParentPlaceholder_WhenJsonRelayDiscoversChildFirst()
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
            _ = client.Payloads.Should().OnlyContain(payload => !payload.Name.Contains("tile_p_m", StringComparison.Ordinal));
            _ = client.RemovedSlotIds.Should().NotContain(id => id.Contains("tile_p_m", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Run_ChildFailure_KeepsParentPlaceholderAsFallback()
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
            _ = client.RemovedSlotIds.Should().NotContain(id => id.Contains("tile_p_m", StringComparison.Ordinal));
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
        public async Task Run_Bootstrap_SendsCoarseCoverage_BeforeDetailedChild_WhenSceneStartsEmpty()
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

            _ = summary.StreamedMeshes.Should().Be(2);
            _ = client.Payloads.Should().HaveCount(2);
            _ = client.Payloads[0].Name.Should().Contain("tile_p_");
            _ = client.Payloads[1].Name.Should().Contain("tile_c_");
            _ = client.RemovedSlotIds.Should().Contain(id => id.Contains("tile_p_m", StringComparison.Ordinal));
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

        [Fact]
        public async Task Run_DryRun_PreparesTileContentConcurrently_WhenWorkersConfigured()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") },
                    new Tile { Id = "1", ContentUri = new Uri("https://example.com/b.glb") },
                    new Tile { Id = "2", ContentUri = new Uri("https://example.com/c.glb") },
                    new Tile { Id = "3", ContentUri = new Uri("https://example.com/d.glb") }
                ]
            });

            var tilesSource = new FakeTilesSource(tileset, contentDelay: TimeSpan.FromMilliseconds(75));
            var client = new FakeResoniteSession();
            TileRunCoordinator coordinator = CreateCoordinator(tilesSource, client, maxConcurrentTileProcessing: 4);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: true), CancellationToken.None);

            _ = summary.StreamedMeshes.Should().Be(4);
            _ = tilesSource.MaxConcurrentContentFetches.Should().BeGreaterThan(1);
        }

        [Fact]
        public async Task Run_NonDryRun_KeepsStreamingSequential_WhenWorkersConfigured()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") },
                    new Tile { Id = "1", ContentUri = new Uri("https://example.com/b.glb") },
                    new Tile { Id = "2", ContentUri = new Uri("https://example.com/c.glb") }
                ]
            });

            var tilesSource = new FakeTilesSource(tileset, contentDelay: TimeSpan.FromMilliseconds(40));
            var client = new FakeResoniteSession(streamDelay: TimeSpan.FromMilliseconds(20));
            TileRunCoordinator coordinator = CreateCoordinator(tilesSource, client, maxConcurrentTileProcessing: 3);

            RunSummary summary = await coordinator.RunAsync(CreateRequest(dryRun: false), CancellationToken.None);

            _ = summary.StreamedMeshes.Should().Be(3);
            _ = tilesSource.MaxConcurrentContentFetches.Should().BeGreaterThan(1);
            _ = client.MaxConcurrentStreams.Should().Be(1);
        }

        [Fact]
        public async Task RunInteractive_PartialSend_RollsBackAndRetriesTile()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "multi", ContentUri = new Uri("https://example.com/multi.glb") }
                ]
            });

            var client = new FakeResoniteSession(failOnSendNumber: 2);
            TileRunCoordinator coordinator = CreateCoordinator(
                new FakeTilesSource(
                    tileset,
                    tileContentByUri: new Dictionary<string, byte[]>
                    {
                        ["https://example.com/multi.glb"] = [9]
                    }),
                client,
                new FakeExtractor(meshCountByMarker: new Dictionary<byte, int>
                {
                    [9] = 2
                }));

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal), RemoveOutOfRangeTiles: false),
                CancellationToken.None);

            _ = client.SendCount.Should().Be(4);
            _ = result.Summary.StreamedMeshes.Should().Be(2);
            _ = result.Summary.FailedTiles.Should().Be(1);
            _ = result.VisibleTiles.Should().ContainKey(StableId("multi"));
            _ = result.VisibleTiles[StableId("multi")].SlotIds.Should().HaveCount(2);
            _ = client.RemovedSlotIds.Should().Contain(id => id.Contains("tile_multi_", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Run_MetadataUpdateFailure_DoesNotPreventCompletion()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile { Id = "0", ContentUri = new Uri("https://example.com/a.glb") }
                ]
            });

            var client = new FakeResoniteSession(failProgressUpdates: true);
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            RunSummary summary = await coordinator.RunAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                CancellationToken.None);

            _ = summary.StreamedMeshes.Should().Be(1);
            _ = client.SendCount.Should().Be(1);
            _ = client.ProgressUpdates.Should().NotBeEmpty();
        }

        [Fact]
        public async Task RunInteractive_RemovalFailure_DoesNotLivelock_AndKeepsRemainingVisibleTile()
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

            var client = new FakeResoniteSession(failOnRemoveContains: "tile_p_m");
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false, maxDepth: 16, bootstrapRangeMultiplier: 0.5d),
                new InteractiveRunInput(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal), RemoveOutOfRangeTiles: false),
                CancellationToken.None);

            _ = result.Summary.StreamedMeshes.Should().Be(2);
            _ = result.Summary.FailedTiles.Should().Be(1);
            _ = result.VisibleTiles.Should().ContainKey(StableId("p"));
            _ = result.VisibleTiles.Should().ContainKey(StableId("c"));
        }

        [Fact]
        public async Task RunInteractive_CleanupEnabled_PreservesOnlyFailedRetainedSlots_OnPartialCleanupFailure()
        {
            var tileset = new Tileset(new Tile
            {
                Id = "root"
            });

            var client = new FakeResoniteSession(failOnRemoveContains: "slot_keep");
            TileRunCoordinator coordinator = CreateCoordinator(new FakeTilesSource(tileset), client);
            string stableId = StableId("retained");
            var retainedTiles = new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [stableId] = new(stableId, "retained", null, [], ["slot_remove", "slot_keep"], "Google; Airbus")
            };

            InteractiveTileRunResult result = await coordinator.RunInteractiveAsync(
                CreateRequest(dryRun: false, manageConnection: false),
                new InteractiveRunInput(retainedTiles, RemoveOutOfRangeTiles: true),
                CancellationToken.None);

            _ = client.RemovedSlotIds.Should().Contain("slot_remove");
            _ = result.VisibleTiles.Should().ContainKey(stableId);
            _ = result.VisibleTiles[stableId].SlotIds.Should().Equal("slot_keep");
        }

        private static TileRunCoordinator CreateCoordinator(
            ITilesSource tilesSource,
            FakeResoniteSession session,
            IGlbMeshExtractor? extractor = null,
            int maxConcurrentTileProcessing = 1)
        {
            var transformer = new PassThroughTransformer();
            return new TileRunCoordinator(
                tilesSource,
                new TraversalCore(new TileSelector(transformer)),
                new TileContentProcessor(tilesSource, extractor ?? new FakeExtractor()),
                new MeshPlacementService(transformer),
                session,
                new FakeGoogleAccessTokenProvider(),
                NullLogger<TileRunCoordinator>.Instance,
                maxConcurrentTileProcessing);
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
                new GeoReference(0d, 0d, 0d),
                new TraversalOptions(500d, maxTiles, maxDepth, 40d, bootstrapRangeMultiplier),
                new ResoniteOutputOptions("127.0.0.1", 12345, dryRun, manageConnection),
                "k");
        }

        private static string StableId(string id)
        {
            return $"0:|{id.Length}:{id}";
        }

        private static string StableId(string prefix, string id)
        {
            return $"{prefix.Length}:{prefix}|{id.Length}:{id}";
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
            IReadOnlyDictionary<string, byte[]>? tileContentByUri = null,
            TimeSpan? contentDelay = null,
            bool ignoreCancellationDuringContent = false) : ITilesSource
        {
            private readonly Tileset _tileset = tileset;
            private readonly IReadOnlyDictionary<string, Tileset> _nestedTilesets = nestedTilesets ?? new Dictionary<string, Tileset>();
            private readonly IReadOnlyDictionary<string, byte[]> _tileContentByUri = tileContentByUri ?? new Dictionary<string, byte[]>();
            private readonly TimeSpan _contentDelay = contentDelay ?? TimeSpan.Zero;
            private readonly bool _ignoreCancellationDuringContent = ignoreCancellationDuringContent;
            private int _activeContentFetches;
            private int _maxConcurrentContentFetches;

            public int MaxConcurrentContentFetches => _maxConcurrentContentFetches;

            public int ContentFetchCount { get; private set; }

            public Task<Tileset> FetchRootTilesetAsync(GoogleTilesAuth auth, CancellationToken cancellationToken)
            {
                return Task.FromResult(_tileset);
            }

            public async Task<FetchedNodeContent> FetchNodeContentAsync(Uri contentUri, GoogleTilesAuth auth, CancellationToken cancellationToken)
            {
                int current = Interlocked.Increment(ref _activeContentFetches);
                UpdateMaxConcurrentContentFetches(current);
                ContentFetchCount++;

                try
                {
                    if (_contentDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(
                            _contentDelay,
                            _ignoreCancellationDuringContent ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
                    }

                    return
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
                    };
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _activeContentFetches);
                }
            }

            private void UpdateMaxConcurrentContentFetches(int current)
            {
                int observed;
                do
                {
                    observed = _maxConcurrentContentFetches;
                    if (current <= observed)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref _maxConcurrentContentFetches, current, observed) != observed);
            }
        }

        private sealed class FakeExtractor(
            IReadOnlyDictionary<byte, string>? attributionByMarker = null,
            IReadOnlyDictionary<byte, int>? meshCountByMarker = null) : IGlbMeshExtractor
        {
            private readonly IReadOnlyDictionary<byte, string> _attributionByMarker = attributionByMarker ?? new Dictionary<byte, string>();
            private readonly IReadOnlyDictionary<byte, int> _meshCountByMarker = meshCountByMarker ?? new Dictionary<byte, int>();

            public GlbExtractResult Extract(byte[] glbBytes)
            {
                byte marker = glbBytes.Length > 0 ? glbBytes[0] : (byte)0;
                _ = _attributionByMarker.TryGetValue(marker, out string? attribution);
                int meshCount = _meshCountByMarker.TryGetValue(marker, out int configuredMeshCount)
                    ? configuredMeshCount
                    : 1;

                MeshData[] meshes = Enumerable.Range(0, meshCount)
                    .Select(index => new MeshData(
                        $"m{index}",
                        [new Vector3d(1d, 2d, 3d), new Vector3d(2d, 2d, 3d), new Vector3d(1d, 3d, 3d)],
                        [0, 1, 2],
                        [new Vector2d(0d, 0d), new Vector2d(1d, 0d), new Vector2d(0d, 1d)],
                        true,
                        Matrix4x4d.Identity,
                        null,
                        null))
                    .ToArray();

                return new GlbExtractResult(meshes, attribution);
            }
        }

        private sealed class FakeResoniteSession(
            bool failFirstSend = false,
            string? failOnNameContains = null,
            TimeSpan? streamDelay = null,
            Action<PlacedMeshPayload, int>? onStreamCompleted = null,
            int? failOnSendNumber = null,
            bool ignoreCancellationDuringStream = false,
            string? failOnRemoveContains = null,
            bool failProgressUpdates = false,
            bool failLicenseUpdates = false) : IResoniteSession
        {
            private readonly bool _failFirstSend = failFirstSend;
            private readonly string? _failOnNameContains = failOnNameContains;
            private readonly TimeSpan _streamDelay = streamDelay ?? TimeSpan.Zero;
            private readonly Action<PlacedMeshPayload, int>? _onStreamCompleted = onStreamCompleted;
            private readonly int? _failOnSendNumber = failOnSendNumber;
            private readonly bool _ignoreCancellationDuringStream = ignoreCancellationDuringStream;
            private readonly string? _failOnRemoveContains = failOnRemoveContains;
            private readonly bool _failProgressUpdates = failProgressUpdates;
            private readonly bool _failLicenseUpdates = failLicenseUpdates;
            private int _activeStreams;
            private int _maxConcurrentStreams;

            public int ConnectCount { get; private set; }
            public int DisconnectCount { get; private set; }
            public int MaxConcurrentStreams => _maxConcurrentStreams;
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
                if (_failLicenseUpdates)
                {
                    throw new InvalidOperationException("synthetic license update failure");
                }

                return Task.CompletedTask;
            }

            public Task SetProgressAsync(string? parentSlotId, float progress01, string progressText, CancellationToken cancellationToken)
            {
                ProgressUpdates.Add((parentSlotId, progress01, progressText));
                if (_failProgressUpdates)
                {
                    throw new InvalidOperationException("synthetic progress update failure");
                }

                return Task.CompletedTask;
            }

            public async Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
            {
                int current = Interlocked.Increment(ref _activeStreams);
                UpdateMaxConcurrentStreams(current);

                SendCount++;
                Payloads.Add(payload);

                try
                {
                    if (_streamDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(
                            _streamDelay,
                            _ignoreCancellationDuringStream ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
                    }

                    if ((_failFirstSend && SendCount == 1) ||
                        (_failOnSendNumber is not null && SendCount == _failOnSendNumber.Value) ||
                        (!string.IsNullOrWhiteSpace(_failOnNameContains) &&
                         payload.Name.Contains(_failOnNameContains, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException("synthetic send failure");
                    }

                    _onStreamCompleted?.Invoke(payload, SendCount);
                    return $"slot_{SendCount}_{payload.Name}";
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _activeStreams);
                }
            }

            public Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
            {
                if (!string.IsNullOrWhiteSpace(_failOnRemoveContains) &&
                    slotId.Contains(_failOnRemoveContains, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("synthetic remove failure");
                }

                RemoveCount++;
                RemovedSlotIds.Add(slotId);
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                DisconnectCount++;
                return Task.CompletedTask;
            }

            private void UpdateMaxConcurrentStreams(int current)
            {
                int observed;
                do
                {
                    observed = _maxConcurrentStreams;
                    if (current <= observed)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref _maxConcurrentStreams, current, observed) != observed);
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
