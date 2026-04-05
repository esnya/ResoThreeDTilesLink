using System.Numerics;
using FluentAssertions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class TraversalCoreTests
    {
        [Fact]
        public void PlanDiscovery_PrioritizesDeeperTiles()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: true), interactive: null);
            WriterState writerState = new();

            IReadOnlyList<DiscoveryWorkItem> work = core.PlanDiscovery(facts, writerState, availableSlots: 2);

            _ = work.Should().HaveCount(2);
            _ = work[0].Tile.TileId.Should().Be("c");
            _ = work[1].Tile.TileId.Should().Be("p");
        }

        [Fact]
        public void Initialize_UsesTraversalDetailTarget_RegardlessOfBootstrapMultiplier()
        {
            var selector = new CapturingSelector();
            var core = new TraversalCore(selector);

            DiscoveryFacts facts = core.Initialize(
                CreateRootTileset(),
                CreateRequest(dryRun: true, bootstrapRangeMultiplier: 0.5d),
                interactive: null);

            _ = facts.Branches.Should().ContainKey(StableId("p"));
            _ = facts.Branches.Should().ContainKey(StableId("c"));
            _ = selector.DetailTargets.Should().ContainSingle().Which.Should().Be(40d);
        }

        [Fact]
        public void ComputeDesiredView_DescendantPreparedFirst_SuppressesParentSend()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: true), interactive: null);
            WriterState writerState = new();
            MarkPrepared(facts, "c", CreatePreparedContent("c"), order: 0);
            MarkPrepared(facts, "p", CreatePreparedContent("p", parentTileId: null, hasChildren: true), order: 1);

            DesiredView desired = core.ComputeDesiredView(facts, writerState);
            WriterCommand? writerCommand = core.PlanWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(2, 0, 0, 0),
                dryRun: true);

            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(StableId("c"));
            _ = writerCommand.Should().BeOfType<SendTileWriterCommand>()
                .Which.Content.Tile.TileId.Should().Be("c");
        }

        [Fact]
        public void PlanWriterCommand_KeepsVisibleParentUntilChildBecomesVisible()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent")
            });
            MarkPrepared(facts, "c", CreatePreparedContent("c"), order: 0);

            DesiredView desired = core.ComputeDesiredView(facts, writerState);
            WriterCommand? firstCommand = core.PlanWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(2, 1, 1, 0),
                dryRun: false);

            _ = firstCommand.Should().BeOfType<SendTileWriterCommand>()
                .Which.Content.Tile.TileId.Should().Be("c");

            writerState.VisibleTiles[StableId("c")] = new RetainedTileState(
                StableId("c"),
                "c",
                StableId("p"),
                [StableId("p")],
                ["slot_child"],
                "Google; Child");

            desired = core.ComputeDesiredView(facts, writerState);
            WriterCommand? removal = core.PlanWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(2, 2, 2, 0),
                dryRun: false);

            _ = removal.Should().BeOfType<RemoveTileWriterCommand>()
                .Which.StableId.Should().Be(StableId("p"));
        }

        [Fact]
        public void PlanWriterCommand_KeepsVisibleParentUntilAllRelevantChildBranchesBecomeVisible()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 60d),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 60d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent")
            });
            MarkPrepared(facts, "c0", CreatePreparedContent("c0"), order: 0);
            MarkPrepared(facts, "c1", CreatePreparedContent("c1"), order: 1);

            DesiredView desired = core.ComputeDesiredView(facts, writerState);
            writerState.VisibleTiles[StableId("c0")] = new RetainedTileState(
                StableId("c0"),
                "c0",
                StableId("p"),
                [StableId("p")],
                ["slot_child_0"],
                "Google; Child 0");

            WriterCommand? nextCommand = core.PlanWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 2, 2, 0),
                dryRun: false);

            _ = nextCommand.Should().BeOfType<SendTileWriterCommand>()
                .Which.Content.Tile.TileId.Should().Be("c1");

            writerState.VisibleTiles[StableId("c1")] = new RetainedTileState(
                StableId("c1"),
                "c1",
                StableId("p"),
                [StableId("p")],
                ["slot_child_1"],
                "Google; Child 1");

            desired = core.ComputeDesiredView(facts, writerState);
            WriterCommand? removal = core.PlanWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 3, 3, 0),
                dryRun: false);

            _ = removal.Should().BeOfType<RemoveTileWriterCommand>()
                .Which.StableId.Should().Be(StableId("p"));
        }

        [Fact]
        public void ComputeDesiredView_VisibleParent_UsesIntermediateChildBeforePreparedLeaf()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 240d),
                CreateTile("g", "https://example.com/g.glb", depth: 2, parentTileId: "c", hasChildren: false, span: 60d, parentStableId: StableId("c"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent")
            });
            MarkPrepared(facts, "c", CreatePreparedContent("c", hasChildren: true), order: 1);
            MarkPrepared(
                facts,
                "g",
                CreatePreparedContent("g", parentTileId: "c", stableId: StableId("g"), parentStableId: StableId("c")),
                order: 0,
                stableId: StableId("g"));

            DesiredView desired = core.ComputeDesiredView(facts, writerState);
            WriterCommand? writerCommand = core.PlanWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 1, 1, 0),
                dryRun: false);

            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(StableId("c"));
            _ = writerCommand.Should().BeOfType<SendTileWriterCommand>()
                .Which.Content.Tile.TileId.Should().Be("c");
        }

        [Fact]
        public void ComputeDesiredView_VisibleIntermediateTile_HidesBehindVisibleGrandchild_WhenAncestorAlreadyRemoved()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 240d),
                CreateTile("g", "https://example.com/g.glb", depth: 2, parentTileId: "c", hasChildren: false, span: 60d, parentStableId: StableId("c"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("c")] = new(StableId("c"), "c", StableId("p"), [StableId("p")], ["slot_child"], "Google; Child"),
                [StableId("g")] = new(StableId("g"), "g", StableId("c"), [StableId("p"), StableId("c")], ["slot_grandchild"], "Google; Grandchild")
            });

            DesiredView desired = core.ComputeDesiredView(facts, writerState);
            WriterCommand? writerCommand = core.PlanWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 2, 2, 0),
                dryRun: false);

            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(StableId("g"));
            _ = writerCommand.Should().BeOfType<RemoveTileWriterCommand>()
                .Which.StableId.Should().Be(StableId("c"));
        }

        [Fact]
        public void PlanWriterCommand_PrefersReplacingCoarserVisibleBranch_BeforeDeeperRefinement()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p0", hasChildren: true, span: 240d, stableId: StableId("c0"), parentStableId: StableId("p0")),
                CreateTile("g0", "https://example.com/g0.glb", depth: 2, parentTileId: "c0", hasChildren: false, span: 60d, stableId: StableId("g0"), parentStableId: StableId("c0")),
                CreateTile("p1", "https://example.com/p1.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d, stableId: StableId("p1")),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p1", hasChildren: false, span: 240d, stableId: StableId("c1"), parentStableId: StableId("p1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("c0")] = new(StableId("c0"), "c0", StableId("p0"), [StableId("p0")], ["slot_c0"], "Google; c0"),
                [StableId("p1")] = new(StableId("p1"), "p1", null, [], ["slot_p1"], "Google; p1")
            });
            MarkPrepared(
                facts,
                "g0",
                CreatePreparedContent("g0", parentTileId: "c0", stableId: StableId("g0"), parentStableId: StableId("c0")),
                order: 0,
                stableId: StableId("g0"));
            MarkPrepared(
                facts,
                "c1",
                CreatePreparedContent("c1", parentTileId: "p1", stableId: StableId("c1"), parentStableId: StableId("p1")),
                order: 1,
                stableId: StableId("c1"));

            DesiredView desired = core.ComputeDesiredView(facts, writerState);
            WriterCommand? writerCommand = core.PlanWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(5, 2, 2, 0),
                dryRun: false);

            _ = desired.StableIds.Should().Contain([StableId("g0"), StableId("c1")]);
            _ = writerCommand.Should().BeOfType<SendTileWriterCommand>()
                .Which.Content.Tile.TileId.Should().Be("c1");
        }

        [Fact]
        public void ComputeDesiredView_ParentRemainsVisible_WhenNoDescendantPrepared()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: true), interactive: null);
            WriterState writerState = new();
            MarkPrepared(facts, "p", CreatePreparedContent("p", parentTileId: null, hasChildren: true), order: 0);

            DesiredView desired = core.ComputeDesiredView(facts, writerState);

            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(StableId("p"));
        }

        [Fact]
        public void PlanDiscovery_RetainedDescendant_SuppressesAncestorPreparation()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d)
            ]);
            string parentStableId = StableId("p");
            string childStableId = StableId("c");

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [childStableId] = new(childStableId, "c", parentStableId, [parentStableId], ["slot_child"], "Google; Child")
            });

            IReadOnlyList<DiscoveryWorkItem> work = core.PlanDiscovery(facts, writerState, availableSlots: 2);
            DesiredView desired = core.ComputeDesiredView(facts, writerState);

            _ = work.Should().BeEmpty();
            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(childStableId);
        }

        [Fact]
        public void ComputeDesiredView_CleanupEnabled_IgnoresOutOfRangeRetainedDescendant()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d)
            ]);
            string parentStableId = StableId("p");
            string childStableId = StableId("c");

            DiscoveryFacts facts = core.Initialize(
                CreateRootTileset(),
                CreateRequest(dryRun: false),
                new InteractiveRunInput(
                    new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
                    {
                        [childStableId] = new(childStableId, "c", parentStableId, [parentStableId], ["slot_child"], "Google; Child")
                    },
                    RemoveOutOfRangeTiles: true));
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [childStableId] = new(childStableId, "c", parentStableId, [parentStableId], ["slot_child"], "Google; Child")
            });
            MarkPrepared(facts, "p", CreatePreparedContent("p", parentTileId: null, hasChildren: true), order: 0);

            DesiredView desired = core.ComputeDesiredView(facts, writerState);

            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(parentStableId);
        }

        [Fact]
        public void ComputeDesiredView_CleanupDisabled_RetainedOutOfRangeDescendantSuppressesAncestor()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d)
            ]);
            string parentStableId = StableId("p");
            string childStableId = StableId("c");

            DiscoveryFacts facts = core.Initialize(
                CreateRootTileset(),
                CreateRequest(dryRun: false),
                new InteractiveRunInput(
                    new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
                    {
                        [childStableId] = new(childStableId, "c", parentStableId, [parentStableId], ["slot_child"], "Google; Child")
                    },
                    RemoveOutOfRangeTiles: false));
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [childStableId] = new(childStableId, "c", parentStableId, [parentStableId], ["slot_child"], "Google; Child")
            });
            MarkPrepared(facts, "p", CreatePreparedContent("p", parentTileId: null, hasChildren: true), order: 0);

            DesiredView desired = core.ComputeDesiredView(facts, writerState);
            IReadOnlyList<DiscoveryWorkItem> work = core.PlanDiscovery(facts, writerState, availableSlots: 1);

            _ = desired.StableIds.Should().BeEmpty();
            _ = work.Should().BeEmpty();
        }

        [Fact]
        public void Initialize_CachedNestedTileset_ExpandsRelayBranch()
        {
            TraversalCore core = CreateCore(prefix => prefix switch
            {
                "" =>
                [
                    CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 200d),
                    CreateTile("j", "https://example.com/nested.json", depth: 1, parentTileId: "p", hasChildren: false, span: 80d)
                ],
                "j" =>
                [
                    CreateTile("leaf", "https://example.com/leaf.glb", depth: 2, parentTileId: "j", hasChildren: false, span: 20d, stableId: StableId("j", "leaf"), parentStableId: StableId("j"))
                ],
                _ => []
            });
            var checkpoint = new InteractiveRunCheckpoint(new Dictionary<string, Tileset>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://example.com/nested.json"] = new Tileset(new Tile { Id = "nestedRoot" })
            });

            DiscoveryFacts facts = core.Initialize(
                CreateRootTileset(),
                CreateRequest(dryRun: true),
                new InteractiveRunInput(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal), RemoveOutOfRangeTiles: false, checkpoint));
            WriterState writerState = new();
            MarkPrepared(facts, "p", CreatePreparedContent("p", parentTileId: null, hasChildren: true), order: 1);
            MarkPrepared(facts, "leaf", CreatePreparedContent("leaf", parentTileId: "j", stableId: StableId("j", "leaf"), parentStableId: StableId("j")), order: 0, stableId: StableId("j", "leaf"));

            DesiredView desired = core.ComputeDesiredView(facts, writerState);

            _ = facts.Branches.Should().ContainKey(StableId("j", "leaf"));
            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(StableId("j", "leaf"));
        }

        private static TraversalCore CreateCore(Func<string, IReadOnlyList<TileSelectionResult>> selectByPrefix)
        {
            return new TraversalCore(new FakeSelector(selectByPrefix));
        }

        private static TileRunRequest CreateRequest(bool dryRun, double bootstrapRangeMultiplier = 4d)
        {
            return new TileRunRequest(
                new GeoReference(0d, 0d, 0d),
                new GeoReference(0d, 0d, 0d),
                new TraversalOptions(500d, 16, 16, 40d, bootstrapRangeMultiplier),
                new ResoniteOutputOptions("127.0.0.1", 12000, dryRun),
                "k");
        }

        private static Tileset CreateRootTileset()
        {
            return new Tileset(new Tile { Id = "root" });
        }

        private static TileSelectionResult CreateTile(
            string tileId,
            string contentUri,
            int depth,
            string? parentTileId,
            bool hasChildren,
            double span,
            string? stableId = null,
            string? parentStableId = null)
        {
            stableId ??= parentTileId switch
            {
                null => StableId(tileId),
                "p" => StableId(tileId),
                _ => StableId(tileId)
            };
            parentStableId ??= parentTileId switch
            {
                null => null,
                "p" => StableId("p"),
                "j" => StableId("j"),
                _ => parentTileId
            };

            return new TileSelectionResult(
                tileId,
                new Uri(contentUri),
                Matrix4x4d.Identity,
                depth,
                parentTileId,
                contentUri.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? TileContentKind.Json : TileContentKind.Glb,
                hasChildren,
                span,
                [],
                stableId,
                parentStableId);
        }

        private static PreparedTileContent CreatePreparedContent(
            string tileId,
            string? parentTileId = "p",
            bool hasChildren = false,
            string? stableId = null,
            string? parentStableId = null)
        {
            TileSelectionResult tile = CreateTile(
                tileId,
                $"https://example.com/{tileId}.glb",
                tileId == "p" ? 0 : tileId == "leaf" ? 2 : 1,
                parentTileId,
                hasChildren,
                tileId == "p" ? 120d : 40d,
                stableId: stableId,
                parentStableId: parentStableId);

            return new PreparedTileContent(
                tile,
                [
                    new PlacedMeshPayload(
                        $"tile_{tileId}",
                        [new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f)],
                        [0, 1, 2],
                        [],
                        false,
                        Vector3.Zero,
                        Quaternion.Identity,
                        Vector3.One,
                        null,
                        null)
                ],
                $"Google; {tileId}");
        }

        private static void MarkPrepared(DiscoveryFacts facts, string tileId, PreparedTileContent content, long order, string? stableId = null)
        {
            stableId ??= tileId switch
            {
                "c" => StableId("c"),
                _ => StableId(tileId)
            };

            TileBranchFact fact = facts.Branches[stableId];
            fact.PrepareStatus = ContentDiscoveryStatus.Ready;
            fact.PreparedContent = content;
            fact.PreparedOrder = order;
            fact.AssetCopyright = content.AssetCopyright;
        }

        private static string StableId(params string[] segments)
        {
            string prefix = string.Empty;
            string stableId = string.Empty;
            foreach (string segment in segments)
            {
                stableId = $"{prefix.Length}:{prefix}|{segment.Length}:{segment}";
                prefix += segment;
            }

            return stableId;
        }

        private sealed class FakeSelector(Func<string, IReadOnlyList<TileSelectionResult>> selectByPrefix) : ITileSelector
        {
            private readonly Func<string, IReadOnlyList<TileSelectionResult>> _selectByPrefix = selectByPrefix;

            public IReadOnlyList<TileSelectionResult> Select(
                Tileset tileset,
                GeoReference reference,
                QueryRange range,
                int maxDepth,
                double detailTargetM,
                int maxTiles,
                Matrix4x4d rootParentWorld,
                string idPrefix,
                int depthOffset,
                string? parentContentTileId,
                string? parentContentStableId)
            {
                return _selectByPrefix(idPrefix);
            }
        }

        private sealed class CapturingSelector : ITileSelector
        {
            public List<double> DetailTargets { get; } = [];

            public IReadOnlyList<TileSelectionResult> Select(
                Tileset tileset,
                GeoReference reference,
                QueryRange range,
                int maxDepth,
                double detailTargetM,
                int maxTiles,
                Matrix4x4d rootParentWorld,
                string idPrefix,
                int depthOffset,
                string? parentContentTileId,
                string? parentContentStableId)
            {
                DetailTargets.Add(detailTargetM);
                return
                [
                    CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 100d),
                    CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 10d)
                ];
            }
        }
    }
}
