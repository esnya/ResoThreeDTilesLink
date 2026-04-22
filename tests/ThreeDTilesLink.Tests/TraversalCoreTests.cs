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

            List<DiscoveryWorkItem> work = core.PlanDiscovery(facts, writerState.CreateSelectionState(), availableSlots: 2);

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
            SceneReconcilerCore reconciler = CreateReconciler(core);
            MarkPrepared(facts, "c", CreatePreparedContent("c"), order: 0);
            MarkPrepared(facts, "p", CreatePreparedContent("p", parentTileId: null, hasChildren: true), order: 1);

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
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

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false, rangeM: 100d), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            MarkPrepared(facts, "c", CreatePreparedContent("c"), order: 0);

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? firstCommand = reconciler.PlanNextWriterCommand(
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

            desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? removal = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(2, 2, 2, 0),
                dryRun: false);

            _ = removal.Should().BeOfType<RemoveTileWriterCommand>()
                .Which.StableId.Should().Be(StableId("p"));
        }

        [Fact]
        public void PlanWriterCommand_KeepsParentUntilAllReplacementChildrenBecomeVisible()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("a", "https://example.com/a.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d),
                CreateTile("b", "https://example.com/b.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("a")] = new(StableId("a"), "a", StableId("p"), [StableId("p")], ["slot_a"], "Google; A")
            });
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
            writerState.VisibleSinceByStableId[StableId("p")] = now.AddSeconds(-10);
            writerState.VisibleSinceByStableId[StableId("a")] = now.AddSeconds(-10);

            SceneReconcilerCore reconciler = CreateReconciler(core);
            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            WriterCommand? removal = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 2, 1, 0),
                dryRun: false,
                allowSend: false,
                allowMetadata: false);

            _ = desired.StableIds.Should().Contain([StableId("p"), StableId("a")]);
            _ = desired.StableIds.Should().NotContain(StableId("b"));
            _ = removal.Should().BeNull();
        }

        [Fact]
        public void PlanWriterCommand_DoesNotRemoveParentWhileReplacementDescendantSendIsInFlight()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("j", "https://example.com/j.json", depth: 1, parentTileId: "p", hasChildren: true, span: 40d, stableId: StableId("j"), parentStableId: StableId("p")),
                CreateTile("g", "https://example.com/g.glb", depth: 2, parentTileId: "j", hasChildren: false, span: 20d, stableId: StableId("g"), parentStableId: StableId("j"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent")
            });
            _ = writerState.InFlightSendStableIds.Add(StableId("g"));

            SceneReconcilerCore reconciler = CreateReconciler(core);
            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            WriterCommand? removal = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 1, 0, 0),
                dryRun: false,
                allowSend: false,
                allowMetadata: false);

            _ = desired.StableIds.Should().Contain(StableId("p"));
            _ = removal.Should().BeNull();
        }

        [Fact]
        public void ComputeDesiredView_VisibleParent_RemainsWhenSiblingReplacementBranchIsSelectedButNotYetVisible()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("a", "https://example.com/a.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d),
                CreateTile("j", "https://example.com/j.json", depth: 1, parentTileId: "p", hasChildren: true, span: 40d, stableId: StableId("j"), parentStableId: StableId("p")),
                CreateTile("g", "https://example.com/g.glb", depth: 2, parentTileId: "j", hasChildren: false, span: 20d, stableId: StableId("g"), parentStableId: StableId("j"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("a")] = new(StableId("a"), "a", StableId("p"), [StableId("p")], ["slot_a"], "Google; A")
            });

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = desired.StableIds.Should().Contain(StableId("p"));
            _ = desired.StableIds.Should().Contain(StableId("a"));
            _ = desired.StableIds.Should().NotContain(StableId("g"));
        }

        [Fact]
        public void ComputeDesiredView_VisibleParent_RemainsWhenKnownChildIsVisibleButSiblingRelayBranchIsStillUnknown()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("a", "https://example.com/a.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d),
                CreateTile("j", "https://example.com/j.json", depth: 1, parentTileId: "p", hasChildren: true, span: 40d, stableId: StableId("j"), parentStableId: StableId("p"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("a")] = new(StableId("a"), "a", StableId("p"), [StableId("p")], ["slot_a"], "Google; A")
            });

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = desired.StableIds.Should().Contain(StableId("p"));
            _ = desired.StableIds.Should().Contain(StableId("a"));
        }

        [Fact]
        public void PlanWriterCommand_DoesNotRemoveVisibleParent_WhenSiblingRelayBranchIsStillUnknown()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("a", "https://example.com/a.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d),
                CreateTile("j", "https://example.com/j.json", depth: 1, parentTileId: "p", hasChildren: true, span: 40d, stableId: StableId("j"), parentStableId: StableId("p"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("a")] = new(StableId("a"), "a", StableId("p"), [StableId("p")], ["slot_a"], "Google; A")
            });
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
            writerState.VisibleSinceByStableId[StableId("p")] = now.AddSeconds(-10);
            writerState.VisibleSinceByStableId[StableId("a")] = now.AddSeconds(-10);

            SceneReconcilerCore reconciler = CreateReconciler(core);
            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 2, 1, 0),
                dryRun: false,
                allowSend: false,
                allowMetadata: false);

            _ = desired.StableIds.Should().Contain([StableId("p"), StableId("a")]);
            _ = command.Should().BeNull();
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
            SceneReconcilerCore reconciler = CreateReconciler(core);
            MarkPrepared(facts, "c", CreatePreparedContent("c", hasChildren: true), order: 1);
            MarkPrepared(
                facts,
                "g",
                CreatePreparedContent("g", parentTileId: "c", stableId: StableId("g"), parentStableId: StableId("c")),
                order: 0,
                stableId: StableId("g"));

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 1, 1, 0),
                dryRun: false);

            _ = desired.StableIds.Should().Contain([StableId("p"), StableId("c")]);
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
            SceneReconcilerCore reconciler = CreateReconciler(core);

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
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
        public void ComputeDesiredView_VisibleParent_HidesBehindRelayChildBranchWhenRenderableGrandchildIsVisible()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("j", "https://example.com/j.json", depth: 1, parentTileId: "p", hasChildren: true, span: 240d, stableId: StableId("j"), parentStableId: StableId("p")),
                CreateTile("g", "https://example.com/g.glb", depth: 2, parentTileId: "j", hasChildren: false, span: 60d, stableId: StableId("g"), parentStableId: StableId("j"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("g")] = new(StableId("g"), "g", StableId("j"), [StableId("p"), StableId("j")], ["slot_grandchild"], "Google; Grandchild")
            });

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(StableId("g"));
            _ = desired.StableIds.Should().NotContain(StableId("p"));
        }

        [Fact]
        public void ComputeDesiredView_VisibleParent_RemainsUntilAllRelayReplacementGlbsBecomeVisible()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("j", "https://example.com/j.json", depth: 1, parentTileId: "p", hasChildren: true, span: 240d, stableId: StableId("j"), parentStableId: StableId("p")),
                CreateTile("g0", "https://example.com/g0.glb", depth: 2, parentTileId: "j", hasChildren: false, span: 60d, stableId: StableId("g0"), parentStableId: StableId("j")),
                CreateTile("g1", "https://example.com/g1.glb", depth: 2, parentTileId: "j", hasChildren: false, span: 60d, stableId: StableId("g1"), parentStableId: StableId("j"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("g0")] = new(StableId("g0"), "g0", StableId("j"), [StableId("p"), StableId("j")], ["slot_g0"], "Google; Grandchild 0")
            });

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = desired.StableIds.Should().Contain([StableId("p"), StableId("g0")]);
            _ = desired.StableIds.Should().NotContain(StableId("g1"));
        }

        [Fact]
        public void ComputeDesiredView_VisibleParent_HidesAfterAllRelayReplacementGlbsBecomeVisible()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("j", "https://example.com/j.json", depth: 1, parentTileId: "p", hasChildren: true, span: 240d, stableId: StableId("j"), parentStableId: StableId("p")),
                CreateTile("g0", "https://example.com/g0.glb", depth: 2, parentTileId: "j", hasChildren: false, span: 60d, stableId: StableId("g0"), parentStableId: StableId("j")),
                CreateTile("g1", "https://example.com/g1.glb", depth: 2, parentTileId: "j", hasChildren: false, span: 60d, stableId: StableId("g1"), parentStableId: StableId("j"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("g0")] = new(StableId("g0"), "g0", StableId("j"), [StableId("p"), StableId("j")], ["slot_g0"], "Google; Grandchild 0"),
                [StableId("g1")] = new(StableId("g1"), "g1", StableId("j"), [StableId("p"), StableId("j")], ["slot_g1"], "Google; Grandchild 1")
            });

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = desired.StableIds.Should().Contain([StableId("g0"), StableId("g1")]);
            _ = desired.StableIds.Should().NotContain(StableId("p"));
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
            SceneReconcilerCore reconciler = CreateReconciler(core);
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

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
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

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(StableId("p"));
        }

        [Fact]
        public void ComputeDesiredView_InitialBootstrap_PrefersCoverageAncestorOverPreparedLeaf()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 240d),
                CreateTile("g", "https://example.com/g.glb", depth: 2, parentTileId: "c", hasChildren: false, span: 60d, stableId: StableId("g"), parentStableId: StableId("c"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false, rangeM: 1000d), interactive: null);
            WriterState writerState = new();
            MarkPrepared(facts, "p", CreatePreparedContent("p", parentTileId: null, hasChildren: true), order: 0);
            MarkPrepared(facts, "g", CreatePreparedContent("g", parentTileId: "c", stableId: StableId("g"), parentStableId: StableId("c")), order: 1, stableId: StableId("g"));

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

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

            List<DiscoveryWorkItem> work = core.PlanDiscovery(facts, writerState.CreateSelectionState(), availableSlots: 2);
            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = work.Should().BeEmpty();
            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(childStableId);
        }

        [Fact]
        public void PlanDiscovery_LargeRange_PrioritizesCoverageAncestorBeforeLeaf()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 240d),
                CreateTile("g", "https://example.com/g.glb", depth: 2, parentTileId: "c", hasChildren: false, span: 60d, stableId: StableId("g"), parentStableId: StableId("c"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false, rangeM: 1000d), interactive: null);
            WriterState writerState = new();

            List<DiscoveryWorkItem> work = core.PlanDiscovery(facts, writerState.CreateSelectionState(), availableSlots: 2);

            _ = work.Should().NotBeEmpty();
            _ = work[0].Should().BeOfType<PrepareTileWorkItem>().Which.Tile.TileId.Should().Be("p");
        }

        [Fact]
        public void PlanDiscovery_InitialBootstrap_DoesNotSuppressCoverageParentBehindNestedRelay()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("j", "https://example.com/j.json", depth: 1, parentTileId: "p", hasChildren: true, span: 60d, parentStableId: StableId("p"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false, rangeM: 60d), interactive: null);
            WriterState writerState = new();

            List<DiscoveryWorkItem> work = core.PlanDiscovery(
                facts,
                writerState.CreateSelectionState(),
                availableNestedSlots: 1,
                availablePrepareSlots: 1);

            _ = work.OfType<PrepareTileWorkItem>().Should().ContainSingle().Which.Tile.TileId.Should().Be("p");
            _ = work.OfType<LoadNestedTilesetWorkItem>().Should().ContainSingle().Which.Tile.TileId.Should().Be("j");
        }

        [Fact]
        public void PlanDiscovery_B3dmLeaf_SchedulesPrepare()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.json", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("leaf", "https://example.com/leaf.b3dm", depth: 1, parentTileId: "p", hasChildren: false, span: 40d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false, rangeM: 60d), interactive: null);
            WriterState writerState = new();

            List<DiscoveryWorkItem> work = core.PlanDiscovery(
                facts,
                writerState.CreateSelectionState(),
                availableNestedSlots: 1,
                availablePrepareSlots: 1);

            _ = work.OfType<PrepareTileWorkItem>().Should().ContainSingle().Which.Tile.TileId.Should().Be("leaf");
        }

        [Fact]
        public void PlanDiscovery_VisibleDescendantStillSchedulesCoverageParent_WhenRangeExpansionNeedsSiblingBranch()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 3000d),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 40d)
            ]);
            string childStableId = StableId("c0");

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [childStableId] = new(childStableId, "c0", StableId("p"), [StableId("p")], ["slot_child"], "Google; Child")
            });

            List<DiscoveryWorkItem> work = core.PlanDiscovery(facts, writerState.CreateSelectionState(), availableSlots: 2);

            _ = work.Should().HaveCount(2);
            _ = work[0].Tile.TileId.Should().Be("p");
            _ = work[1].Tile.TileId.Should().Be("c1");
        }

        [Fact]
        public void PlanDiscovery_SeparateNestedAndPrepareSlots_SchedulesBothKinds()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("relay", "https://example.com/relay.json", depth: 0, parentTileId: null, hasChildren: true, span: 3000d),
                CreateTile("render", "https://example.com/render.glb", depth: 0, parentTileId: null, hasChildren: false, span: 2000d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: true), interactive: null);
            WriterState writerState = new();

            List<DiscoveryWorkItem> work = core.PlanDiscovery(
                facts,
                writerState.CreateSelectionState(),
                availableNestedSlots: 1,
                availablePrepareSlots: 1);

            _ = work.Should().HaveCount(2);
            _ = work.Should().ContainSingle(static item => item is LoadNestedTilesetWorkItem);
            _ = work.Should().ContainSingle(static item => item is PrepareTileWorkItem);
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

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

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

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            List<DiscoveryWorkItem> work = core.PlanDiscovery(facts, writerState.CreateSelectionState(), availableSlots: 1);

            _ = desired.StableIds.Should().BeEmpty();
            _ = work.Should().BeEmpty();
        }

        [Fact]
        public void PlanWriterCommand_KeepsVisibleParentUntilEachDirectChildReplacementGlbBecomesVisible()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 60d),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 60d),
                CreateTile("g0", "https://example.com/g0.glb", depth: 2, parentTileId: "c0", hasChildren: false, span: 30d, stableId: StableId("g0"), parentStableId: StableId("c0")),
                CreateTile("g1", "https://example.com/g1.glb", depth: 2, parentTileId: "c1", hasChildren: false, span: 30d, stableId: StableId("g1"), parentStableId: StableId("c1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("g0")] = new(StableId("g0"), "g0", StableId("c0"), [StableId("p"), StableId("c0")], ["slot_g0"], "Google; Grandchild 0"),
                [StableId("g1")] = new(StableId("g1"), "g1", StableId("c1"), [StableId("p"), StableId("c1")], ["slot_g1"], "Google; Grandchild 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(5, 3, 3, 0),
                dryRun: false,
                allowMetadata: false);

            _ = desired.StableIds.Should().Contain([StableId("p"), StableId("g0"), StableId("g1")]);
            _ = writerCommand.Should().BeNull();
        }

        [Fact]
        public void ComputeDesiredView_CoarseVisibleParent_RemainsUntilReplacementFrontierCompletes()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 400d),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 400d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false, rangeM: 300d), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("c0")] = new(StableId("c0"), "c0", StableId("p"), [StableId("p")], ["slot_c0"], "Google; Child 0")
            });

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = desired.StableIds.Should().Contain(StableId("c0"));
            _ = desired.StableIds.Should().Contain(StableId("p"));
        }

        [Fact]
        public void PlanWriterCommand_RemovesVisibleParent_WhenDesiredAlreadyExcludesIt()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 60d),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 60d),
                CreateTile("g0", "https://example.com/g0.glb", depth: 2, parentTileId: "c0", hasChildren: false, span: 30d, stableId: StableId("g0"), parentStableId: StableId("c0")),
                CreateTile("g1", "https://example.com/g1.glb", depth: 2, parentTileId: "c1", hasChildren: false, span: 30d, stableId: StableId("g1"), parentStableId: StableId("c1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: true), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("g0")] = new(StableId("g0"), "g0", StableId("c0"), [StableId("p"), StableId("c0")], ["slot_g0"], "Google; Grandchild 0")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            MarkPrepared(
                facts,
                "g1",
                CreatePreparedContent("g1", parentTileId: "c1", stableId: StableId("g1"), parentStableId: StableId("c1")),
                order: 0,
                stableId: StableId("g1"));

            DesiredView desired = new(
                new HashSet<string>([StableId("g0"), StableId("g1")], StringComparer.Ordinal),
                facts.Branches.Keys.ToHashSet(StringComparer.Ordinal),
                new HashSet<string>([StableId("g0"), StableId("g1")], StringComparer.Ordinal));
            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(5, 2, 1, 0),
                dryRun: true);

            _ = desired.StableIds.Should().Contain([StableId("g0"), StableId("g1")]);
            _ = desired.StableIds.Should().NotContain(StableId("p"));
            _ = writerCommand.Should().BeOfType<RemoveTileWriterCommand>()
                .Which.StableId.Should().Be(StableId("p"));
        }

        [Fact]
        public void PlanWriterCommand_RemovesVisibleParent_WithoutReplacementDelay()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 60d),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 60d),
                CreateTile("g0", "https://example.com/g0.glb", depth: 2, parentTileId: "c0", hasChildren: false, span: 30d, stableId: StableId("g0"), parentStableId: StableId("c0")),
                CreateTile("g1", "https://example.com/g1.glb", depth: 2, parentTileId: "c1", hasChildren: false, span: 30d, stableId: StableId("g1"), parentStableId: StableId("c1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: true), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("g0")] = new(StableId("g0"), "g0", StableId("c0"), [StableId("p"), StableId("c0")], ["slot_g0"], "Google; Grandchild 0"),
                [StableId("g1")] = new(StableId("g1"), "g1", StableId("c1"), [StableId("p"), StableId("c1")], ["slot_g1"], "Google; Grandchild 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);

            DesiredView desired = new(
                new HashSet<string>([StableId("g0"), StableId("g1")], StringComparer.Ordinal),
                facts.Branches.Keys.ToHashSet(StringComparer.Ordinal),
                new HashSet<string>([StableId("g0"), StableId("g1")], StringComparer.Ordinal));
            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(5, 3, 2, 0),
                dryRun: true);

            _ = desired.StableIds.Should().Contain([StableId("g0"), StableId("g1")]);
            _ = writerCommand.Should().BeOfType<RemoveTileWriterCommand>()
                .Which.StableId.Should().Be(StableId("p"));
        }

        [Fact]
        public void PlanWriterCommand_AllowsUnrelatedRemovalWhileAnotherBranchSendIsInFlight()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d, stableId: StableId("p0")),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p0", hasChildren: false, span: 60d, stableId: StableId("c0"), parentStableId: StableId("p0")),
                CreateTile("p1", "https://example.com/p1.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d, stableId: StableId("p1")),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p1", hasChildren: false, span: 60d, stableId: StableId("c1"), parentStableId: StableId("p1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p0")] = new(StableId("p0"), "p0", null, [], ["slot_p0"], "Google; Parent 0"),
                [StableId("c0")] = new(StableId("c0"), "c0", StableId("p0"), [StableId("p0")], ["slot_c0"], "Google; Child 0"),
                [StableId("p1")] = new(StableId("p1"), "p1", null, [], ["slot_p1"], "Google; Parent 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            _ = writerState.InFlightSendStableIds.Add(StableId("c1"));

            DesiredView desired = new(
                new HashSet<string>([StableId("c0"), StableId("c1")], StringComparer.Ordinal),
                facts.Branches.Keys.ToHashSet(StringComparer.Ordinal),
                new HashSet<string>([StableId("c0"), StableId("c1")], StringComparer.Ordinal));

            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(4, 3, 2, 0),
                dryRun: false);

            _ = writerCommand.Should().BeOfType<RemoveTileWriterCommand>()
                .Which.StableId.Should().Be(StableId("p0"));
        }

        [Fact]
        public void PlanWriterCommand_RemovesVisibleParent_RegardlessOfCandidateSetWhenDesiredExcludesIt()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d),
                CreateTile("c0", "https://example.com/c0.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 60d),
                CreateTile("c1", "https://example.com/c1.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 60d),
                CreateTile("g0", "https://example.com/g0.glb", depth: 2, parentTileId: "c0", hasChildren: false, span: 30d, stableId: StableId("g0"), parentStableId: StableId("c0")),
                CreateTile("g1", "https://example.com/g1.glb", depth: 2, parentTileId: "c1", hasChildren: false, span: 30d, stableId: StableId("g1"), parentStableId: StableId("c1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: true), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent"),
                [StableId("g0")] = new(StableId("g0"), "g0", StableId("c0"), [StableId("p"), StableId("c0")], ["slot_g0"], "Google; Grandchild 0")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            MarkPrepared(
                facts,
                "g1",
                CreatePreparedContent("g1", parentTileId: "c1", stableId: StableId("g1"), parentStableId: StableId("c1")),
                order: 0,
                stableId: StableId("g1"));

            DesiredView desired = new(
                new HashSet<string>([StableId("g0"), StableId("g1")], StringComparer.Ordinal),
                new HashSet<string>([StableId("p"), StableId("c0"), StableId("c1"), StableId("g0"), StableId("g1")], StringComparer.Ordinal),
                new HashSet<string>([StableId("p"), StableId("g0")], StringComparer.Ordinal));
            WriterCommand? writerCommand = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(5, 2, 1, 0),
                dryRun: true);

            _ = writerCommand.Should().BeOfType<RemoveTileWriterCommand>()
                .Which.StableId.Should().Be(StableId("p"));
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

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            _ = facts.Branches.Should().ContainKey(StableId("j", "leaf"));
            _ = desired.StableIds.Should().ContainSingle().Which.Should().Be(StableId("j", "leaf"));
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_UsesCandidateTilesAsConservativeBacklog()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 120d)
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new();
            SceneReconcilerCore reconciler = CreateReconciler(core);

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(5, 2, 0, 0),
                dryRun: false);

            SyncSessionMetadataWriterCommand metadata = command.Should().BeOfType<SyncSessionMetadataWriterCommand>().Subject;
            _ = metadata.ProgressValue.Should().BeApproximately(2f / 6f, 0.0001f);
            _ = metadata.ProgressText.Should().Be("Running...");
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_CountsPreparedButNotVisibleCandidatesInDenominator()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: true, span: 240d),
                CreateTile("g", "https://example.com/g.glb", depth: 2, parentTileId: "c", hasChildren: false, span: 60d, stableId: StableId("g"), parentStableId: StableId("c"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p")] = new(StableId("p"), "p", null, [], ["slot_parent"], "Google; Parent")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            MarkPrepared(
                facts,
                "g",
                CreatePreparedContent("g", parentTileId: "c", stableId: StableId("g"), parentStableId: StableId("c")),
                order: 0,
                stableId: StableId("g"));
            writerState.AppliedLicenseCredit = "Google Maps; Parent";
            writerState.AppliedProgressValue = 0f;
            writerState.AppliedProgressText = "stale";

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(3, 3, 0, 0),
                dryRun: false,
                allowRemoval: false);

            SyncSessionMetadataWriterCommand metadata = command.Should().BeOfType<SyncSessionMetadataWriterCommand>().Subject;
            _ = metadata.ProgressValue.Should().BeApproximately(3f / 5f, 0.0001f);
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_UpdatesWhileSendIsInFlightAfterCadenceAndDelta()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p0")),
                CreateTile("p1", "https://example.com/p1.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p0")] = new(StableId("p0"), "p0", null, [], ["slot_p0"], "Google; Parent 0"),
                [StableId("p1")] = new(StableId("p1"), "p1", null, [], ["slot_p1"], "Google; Parent 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            writerState.AppliedLicenseCredit = "Google Maps; Parent 0; Parent 1";
            writerState.AppliedProgressValue = 0f;
            writerState.AppliedProgressText = "stale";
            writerState.LastMetadataSyncStartedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(500);
            _ = writerState.InFlightSendStableIds.Add(StableId("p1"));

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(16, 8, 1, 0),
                dryRun: false,
                allowRemoval: false,
                allowSend: false);

            SyncSessionMetadataWriterCommand metadata = command.Should().BeOfType<SyncSessionMetadataWriterCommand>().Subject;
            _ = metadata.ProcessedTiles.Should().Be(8);
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_DoesNotUpdateBeforeCadence()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p0")),
                CreateTile("p1", "https://example.com/p1.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p0")] = new(StableId("p0"), "p0", null, [], ["slot_p0"], "Google; Parent 0"),
                [StableId("p1")] = new(StableId("p1"), "p1", null, [], ["slot_p1"], "Google; Parent 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            writerState.AppliedLicenseCredit = "Google Maps; Parent 0; Parent 1";
            writerState.AppliedProgressValue = 0.42f;
            writerState.AppliedProgressText = "Running:";
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(1);
            writerState.LastMetadataSyncStartedAt = now - TimeSpan.FromMilliseconds(100);
            writerState.LastMetadataSyncProcessedTiles = 0;
            writerState.LastMetadataSyncProgressValue = 0.42f;
            _ = writerState.InFlightSendStableIds.Add(StableId("p1"));

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(16, 8, 1, 0),
                dryRun: false,
                allowRemoval: false,
                allowSend: false,
                now: now);

            _ = command.Should().BeNull();
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_DoesNotUpdateBelowDeltaThreshold()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p0")),
                CreateTile("p1", "https://example.com/p1.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p0")] = new(StableId("p0"), "p0", null, [], ["slot_p0"], "Google; Parent 0"),
                [StableId("p1")] = new(StableId("p1"), "p1", null, [], ["slot_p1"], "Google; Parent 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            writerState.AppliedLicenseCredit = "Google Maps; Parent 0; Parent 1";
            writerState.AppliedProgressValue = 0.62f;
            writerState.AppliedProgressText = "Running:";
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(1);
            writerState.LastMetadataSyncStartedAt = now - TimeSpan.FromMilliseconds(500);
            writerState.LastMetadataSyncProcessedTiles = 10;
            writerState.LastMetadataSyncProgressValue = 0.62f;
            _ = writerState.InFlightSendStableIds.Add(StableId("p1"));

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());

            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(16, 12, 1, 0),
                dryRun: false,
                allowRemoval: false,
                allowSend: false,
                now: now);

            _ = command.Should().BeNull();
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_DoesNotRegressWhenBacklogExpands()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p0")),
                CreateTile("p1", "https://example.com/p1.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p0")] = new(StableId("p0"), "p0", null, [], ["slot_p0"], "Google; Parent 0"),
                [StableId("p1")] = new(StableId("p1"), "p1", null, [], ["slot_p1"], "Google; Parent 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            writerState.AppliedLicenseCredit = "Google Maps; Parent 0; Parent 1";
            writerState.AppliedProgressValue = 0.88f;
            writerState.AppliedProgressText = "stale";
            writerState.LastMetadataSyncStartedAt = DateTimeOffset.UnixEpoch;

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(16, 8, 1, 0),
                dryRun: false,
                allowRemoval: false,
                allowSend: false);

            SyncSessionMetadataWriterCommand metadata = command.Should().BeOfType<SyncSessionMetadataWriterCommand>().Subject;
            _ = metadata.ProgressValue.Should().Be(0.88f);
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_AllowsRegressionWhenPreviouslyCompletedRunHasNewBacklog()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p0")),
                CreateTile("p1", "https://example.com/p1.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p0")] = new(StableId("p0"), "p0", null, [], ["slot_p0"], "Google; Parent 0"),
                [StableId("p1")] = new(StableId("p1"), "p1", null, [], ["slot_p1"], "Google; Parent 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            writerState.AppliedLicenseCredit = "Google Maps; Parent 0; Parent 1";
            writerState.AppliedProgressValue = 1f;
            writerState.AppliedProgressText = "Completed: candidate=2 processed=2 streamed=2 failed=0";
            writerState.LastMetadataSyncStartedAt = DateTimeOffset.UnixEpoch;

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(16, 8, 1, 0),
                dryRun: false,
                allowRemoval: false,
                allowSend: false);

            SyncSessionMetadataWriterCommand metadata = command.Should().BeOfType<SyncSessionMetadataWriterCommand>().Subject;
            _ = metadata.ProgressValue.Should().BeLessThan(1f);
            _ = metadata.ProgressValue.Should().BeLessThan(writerState.AppliedProgressValue);
            _ = metadata.ProgressText.Should().Be("Running...");
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_ImmediatelySyncsWhenCompletionStateReverts()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p0")),
                CreateTile("p1", "https://example.com/p1.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p1"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p0")] = new(StableId("p0"), "p0", null, [], ["slot_p0"], "Google; Parent 0"),
                [StableId("p1")] = new(StableId("p1"), "p1", null, [], ["slot_p1"], "Google; Parent 1")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            writerState.AppliedLicenseCredit = "Google Maps; Parent 0; Parent 1";
            writerState.AppliedProgressValue = 1f;
            writerState.AppliedProgressText = "Completed: candidate=2 processed=2 streamed=2 failed=0";
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(1);
            writerState.LastMetadataSyncStartedAt = now - TimeSpan.FromMilliseconds(100);
            writerState.LastMetadataSyncProcessedTiles = 8;
            writerState.LastMetadataSyncProgressValue = 1f;

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(16, 8, 1, 0),
                dryRun: false,
                allowRemoval: false,
                allowSend: false,
                now: now);

            SyncSessionMetadataWriterCommand metadata = command.Should().BeOfType<SyncSessionMetadataWriterCommand>().Subject;
            _ = metadata.ProgressText.Should().Be("Running...");
            _ = metadata.ProgressValue.Should().BeLessThan(1f);
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_UpdatesImmediatelyWhenLicenseChanges()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p0"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new(new Dictionary<string, RetainedTileState>(StringComparer.Ordinal)
            {
                [StableId("p0")] = new(StableId("p0"), "p0", null, [], ["slot_p0"], "Google; Parent 0")
            });
            SceneReconcilerCore reconciler = CreateReconciler(core);
            writerState.AppliedLicenseCredit = "Google Maps";
            writerState.AppliedProgressValue = 0.5f;
            writerState.AppliedProgressText = "Running:";
            writerState.LastMetadataSyncStartedAt = DateTimeOffset.UnixEpoch;

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            WriterCommand? command = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(1, 0, 0, 0),
                dryRun: false,
                allowRemoval: false,
                allowSend: false);

            _ = command.Should().BeOfType<SyncSessionMetadataWriterCommand>();
        }

        [Fact]
        public void PlanWriterCommand_MetadataProgress_UsesCleanupDebtForLicenseAttribution()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("p0", "https://example.com/p0.glb", depth: 0, parentTileId: null, hasChildren: false, span: 120d, stableId: StableId("p0"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new();
            writerState.CleanupDebtTiles[StableId("p0")] = new(
                StableId("p0"),
                "p0",
                null,
                [],
                ["slot_p0"],
                "Google; Parent 0");
            SceneReconcilerCore reconciler = CreateReconciler(core);
            writerState.AppliedLicenseCredit = "Google Maps";
            writerState.AppliedProgressValue = 0.5f;
            writerState.AppliedProgressText = "Running:";
            writerState.LastMetadataSyncStartedAt = DateTimeOffset.UnixEpoch;

            DesiredView desired = core.ComputeDesiredView(facts, writerState.CreateSelectionState());
            SyncSessionMetadataWriterCommand metadata = reconciler.PlanNextWriterCommand(
                facts,
                writerState,
                desired,
                new ProgressSnapshot(1, 0, 0, 0),
                dryRun: false,
                allowRemoval: false,
                allowSend: false).Should().BeOfType<SyncSessionMetadataWriterCommand>().Subject;

            _ = metadata.LicenseCredit.Should().Contain("Parent 0");
            _ = metadata.UpdateLicense.Should().BeTrue();
        }

        [Fact]
        public void ApplyWriterCompletion_ClearsPreparedContent_WhenPartialSendRetriesAreExhausted()
        {
            TraversalCore core = CreateCore(_ =>
            [
                CreateTile("child", "https://example.com/child.glb", depth: 1, parentTileId: "parent", hasChildren: false, span: 120d, stableId: StableId("child"), parentStableId: StableId("parent"))
            ]);

            DiscoveryFacts facts = core.Initialize(CreateRootTileset(), CreateRequest(dryRun: false), interactive: null);
            WriterState writerState = new();
            PreparedTileContent content = CreatePreparedContent("child", parentTileId: "parent", stableId: StableId("child"), parentStableId: StableId("parent"));
            MarkPrepared(facts, "child", content, order: 0, stableId: StableId("child"));
            TileBranchFact fact = facts.Branches[StableId("child")];
            fact.CompleteSendFailureCount = 1;

            int processedTiles = 0;
            int streamedMeshes = 0;
            int failedTiles = 0;

            SceneReconcilerCore.ApplyWriterCompletion(
                facts,
                writerState,
                new SendTileCompleted(content, false, 1, ["slot_partial"], new InvalidOperationException("send failed")),
                dryRun: false,
                ref processedTiles,
                ref streamedMeshes,
                ref failedTiles);

            _ = fact.PrepareStatus.Should().Be(ContentDiscoveryStatus.Failed);
            _ = fact.PreparedContent.Should().BeNull();
        }

        private static TraversalCore CreateCore(Func<string, IReadOnlyList<TileSelectionResult>> selectByPrefix)
        {
            return new TraversalCore(new FakeSelector(selectByPrefix));
        }

        private static SceneReconcilerCore CreateReconciler(TraversalCore _) => new();

        private static TileRunRequest CreateRequest(bool dryRun, double bootstrapRangeMultiplier = 4d, double rangeM = 500d)
        {
            return new TileRunRequest(
                new GeoReference(0d, 0d, 0d),
                new GeoReference(0d, 0d, 0d),
                new TraversalOptions(rangeM, 40d, bootstrapRangeMultiplier),
                new SceneOutputOptions("127.0.0.1", 12000, dryRun),
                new TileSourceOptions(
                    new Uri("https://example.com/root.json"),
                    new TileSourceAccess("k", null)));
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
                contentUri.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? TileContentKind.Json
                    : contentUri.EndsWith(".b3dm", StringComparison.OrdinalIgnoreCase)
                        ? TileContentKind.B3dm
                        : TileContentKind.Glb,
                hasChildren,
                span,
                span,
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
                double detailTargetM,
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
                double detailTargetM,
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
