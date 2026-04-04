using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class DefaultTileStreamingSchedulerTests
    {
        [Fact]
        public void Bootstrap_DefersCoarseGlb_AndStreamsDiscoveredChild()
        {
            var scheduler = CreateScheduler(new FakeSelector(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 120d)
            ]));

            scheduler.Initialize(CreateRootTileset(), CreateOptions(dryRun: true, renderStartSpanRatio: 0.5d));

            bool hasItem = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? workItem);

            _ = hasItem.Should().BeTrue();
            StreamGlbTileWorkItem stream = workItem.Should().BeOfType<StreamGlbTileWorkItem>().Subject;
            _ = stream.Tile.TileId.Should().Be("c");
        }

        [Fact]
        public void DeferredFallback_QueuesParent_WhenNoChildBranchSelected()
        {
            var scheduler = CreateScheduler(new FakeSelector(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d)
            ]));

            scheduler.Initialize(CreateRootTileset(), CreateOptions(dryRun: true, renderStartSpanRatio: 0.5d));

            bool hasItem = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? workItem);

            _ = hasItem.Should().BeTrue();
            StreamGlbTileWorkItem stream = workItem.Should().BeOfType<StreamGlbTileWorkItem>().Subject;
            _ = stream.Tile.TileId.Should().Be("p");
        }

        [Fact]
        public void BranchCompletion_QueuesParentRemoval_WhenChildCompletes()
        {
            var scheduler = CreateScheduler(new FakeSelector(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 100d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 50d)
            ]));

            scheduler.Initialize(CreateRootTileset(), CreateOptions(dryRun: false));

            // Initial session credit update.
            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? initial);
            UpdateLicenseCreditWorkItem initialUpdate = initial.Should().BeOfType<UpdateLicenseCreditWorkItem>().Subject;
            scheduler.HandleResult(new UpdateLicenseCreditWorkResult(initialUpdate.CreditString, true, null));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? parentItem);
            StreamGlbTileWorkItem parentStream = parentItem.Should().BeOfType<StreamGlbTileWorkItem>().Subject;
            scheduler.HandleResult(new StreamGlbTileWorkResult(
                parentStream.Tile,
                StreamGlbOutcome.Success,
                1,
                ["slot_parent"],
                "Google; Parent",
                null));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? parentCreditItem);
            UpdateLicenseCreditWorkItem parentCredit = parentCreditItem.Should().BeOfType<UpdateLicenseCreditWorkItem>().Subject;
            scheduler.HandleResult(new UpdateLicenseCreditWorkResult(parentCredit.CreditString, true, null));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? childItem);
            StreamGlbTileWorkItem childStream = childItem.Should().BeOfType<StreamGlbTileWorkItem>().Subject;
            scheduler.HandleResult(new StreamGlbTileWorkResult(
                childStream.Tile,
                StreamGlbOutcome.Success,
                1,
                ["slot_child"],
                "Google; Child",
                null));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? childCreditItem);
            UpdateLicenseCreditWorkItem childCredit = childCreditItem.Should().BeOfType<UpdateLicenseCreditWorkItem>().Subject;
            scheduler.HandleResult(new UpdateLicenseCreditWorkResult(childCredit.CreditString, true, null));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? removeItem);
            RemoveParentTileSlotsWorkItem remove = removeItem.Should().BeOfType<RemoveParentTileSlotsWorkItem>().Subject;
            _ = remove.TileId.Should().Be("p");
            _ = remove.SlotIds.Should().ContainSingle().Which.Should().Be("slot_parent");
        }

        [Fact]
        public void AttributionUpdate_EmitsInitialAndPostStreamCredits()
        {
            var scheduler = CreateScheduler(new FakeSelector(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: false, span: 100d)
            ]));

            scheduler.Initialize(CreateRootTileset(), CreateOptions(dryRun: false));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? firstItem);
            UpdateLicenseCreditWorkItem initialCredit = firstItem.Should().BeOfType<UpdateLicenseCreditWorkItem>().Subject;
            _ = initialCredit.CreditString.Should().Be("Google Maps");
            scheduler.HandleResult(new UpdateLicenseCreditWorkResult(initialCredit.CreditString, true, null));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? streamItem);
            StreamGlbTileWorkItem stream = streamItem.Should().BeOfType<StreamGlbTileWorkItem>().Subject;
            scheduler.HandleResult(new StreamGlbTileWorkResult(
                stream.Tile,
                StreamGlbOutcome.Success,
                1,
                ["slot_1"],
                "Google; Airbus",
                null));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? secondItem);
            UpdateLicenseCreditWorkItem secondCredit = secondItem.Should().BeOfType<UpdateLicenseCreditWorkItem>().Subject;
            _ = secondCredit.CreditString.Should().Be("Google; Airbus");
        }

        [Fact]
        public void MaxTiles_StopsAfterLimit()
        {
            var scheduler = CreateScheduler(new FakeSelector(_ =>
            [
                CreateTile("a", "https://example.com/a.glb", depth: 0, parentTileId: null, hasChildren: false, span: 100d),
                CreateTile("b", "https://example.com/b.glb", depth: 0, parentTileId: null, hasChildren: false, span: 90d)
            ]));

            scheduler.Initialize(CreateRootTileset(), CreateOptions(dryRun: true, maxTiles: 1));

            _ = scheduler.TryDequeueWorkItem(out SchedulerWorkItem? firstItem).Should().BeTrue();
            StreamGlbTileWorkItem stream = firstItem.Should().BeOfType<StreamGlbTileWorkItem>().Subject;
            scheduler.HandleResult(new StreamGlbTileWorkResult(
                stream.Tile,
                StreamGlbOutcome.Success,
                1,
                [],
                null,
                null));

            _ = scheduler.TryDequeueWorkItem(out _).Should().BeFalse();

            RunSummary summary = scheduler.GetSummary();
            _ = summary.CandidateTiles.Should().Be(1);
            _ = summary.ProcessedTiles.Should().Be(1);
        }

        private static DefaultTileStreamingScheduler CreateScheduler(ITileSelector selector)
        {
            return new DefaultTileStreamingScheduler(selector, NullLogger<DefaultTileStreamingScheduler>.Instance);
        }

        private static StreamerOptions CreateOptions(bool dryRun, int maxTiles = 16, double renderStartSpanRatio = 4d)
        {
            return new StreamerOptions(
                new GeoReference(0d, 0d, 0d),
                500d,
                "127.0.0.1",
                12000,
                maxTiles,
                16,
                40d,
                dryRun,
                "k",
                renderStartSpanRatio);
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
            double span)
        {
            return new TileSelectionResult(
                tileId,
                new Uri(contentUri),
                Matrix4x4d.Identity,
                depth,
                parentTileId,
                contentUri.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? TileContentKind.Json : TileContentKind.Glb,
                hasChildren,
                span,
                []);
        }

        private sealed class FakeSelector(Func<string, IReadOnlyList<TileSelectionResult>> selectByPrefix) : ITileSelector
        {
            private readonly Func<string, IReadOnlyList<TileSelectionResult>> _selectByPrefix = selectByPrefix;

            public IReadOnlyList<TileSelectionResult> Select(
                Tileset tileset,
                GeoReference reference,
                QuerySquare square,
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
    }
}
