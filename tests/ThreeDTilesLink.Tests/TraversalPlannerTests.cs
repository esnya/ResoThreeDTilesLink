using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class TraversalPlannerTests
    {
        [Fact]
        public void Bootstrap_DefersCoarseGlb_AndStreamsDiscoveredChild()
        {
            var planner = CreatePlanner(new FakeSelector(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 120d)
            ]));

            planner.Initialize(CreateRootTileset(), CreateRequest(dryRun: true, bootstrapRangeMultiplier: 0.5d));

            bool hasItem = planner.TryPlanNext(out PlannerCommand? command);

            _ = hasItem.Should().BeTrue();
            ProcessTileContentCommand stream = command.Should().BeOfType<ProcessTileContentCommand>().Subject;
            _ = stream.Tile.TileId.Should().Be("c");
        }

        [Fact]
        public void DeferredFallback_QueuesParent_WhenNoChildBranchSelected()
        {
            var planner = CreatePlanner(new FakeSelector(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 1200d)
            ]));

            planner.Initialize(CreateRootTileset(), CreateRequest(dryRun: true, bootstrapRangeMultiplier: 0.5d));

            bool hasItem = planner.TryPlanNext(out PlannerCommand? command);

            _ = hasItem.Should().BeTrue();
            ProcessTileContentCommand stream = command.Should().BeOfType<ProcessTileContentCommand>().Subject;
            _ = stream.Tile.TileId.Should().Be("p");
        }

        [Fact]
        public void BranchCompletion_QueuesParentRemoval_WhenChildCompletes()
        {
            var planner = CreatePlanner(new FakeSelector(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: true, span: 100d),
                CreateTile("c", "https://example.com/c.glb", depth: 1, parentTileId: "p", hasChildren: false, span: 50d)
            ]));

            planner.Initialize(CreateRootTileset(), CreateRequest(dryRun: false));

            _ = planner.TryPlanNext(out PlannerCommand? initial).Should().BeTrue();
            UpdateLicenseCreditCommand initialUpdate = initial.Should().BeOfType<UpdateLicenseCreditCommand>().Subject;
            planner.ApplyResult(new LicenseUpdatedResult(initialUpdate.CreditString, true, null));

            _ = planner.TryPlanNext(out PlannerCommand? parentItem).Should().BeTrue();
            ProcessTileContentCommand parentStream = parentItem.Should().BeOfType<ProcessTileContentCommand>().Subject;
            planner.ApplyResult(new RenderableContentReadyResult(parentStream.Tile, 1, ["slot_parent"], "Google; Parent"));

            _ = planner.TryPlanNext(out PlannerCommand? parentCreditItem).Should().BeTrue();
            UpdateLicenseCreditCommand parentCredit = parentCreditItem.Should().BeOfType<UpdateLicenseCreditCommand>().Subject;
            planner.ApplyResult(new LicenseUpdatedResult(parentCredit.CreditString, true, null));

            _ = planner.TryPlanNext(out PlannerCommand? childItem).Should().BeTrue();
            ProcessTileContentCommand childStream = childItem.Should().BeOfType<ProcessTileContentCommand>().Subject;
            planner.ApplyResult(new RenderableContentReadyResult(childStream.Tile, 1, ["slot_child"], "Google; Child"));

            _ = planner.TryPlanNext(out PlannerCommand? childCreditItem).Should().BeTrue();
            UpdateLicenseCreditCommand childCredit = childCreditItem.Should().BeOfType<UpdateLicenseCreditCommand>().Subject;
            planner.ApplyResult(new LicenseUpdatedResult(childCredit.CreditString, true, null));

            _ = planner.TryPlanNext(out PlannerCommand? removeItem).Should().BeTrue();
            RemoveSlotsCommand remove = removeItem.Should().BeOfType<RemoveSlotsCommand>().Subject;
            _ = remove.TileId.Should().Be("p");
            _ = remove.SlotIds.Should().ContainSingle().Which.Should().Be("slot_parent");
        }

        [Fact]
        public void AttributionUpdate_EmitsInitialAndPostStreamCredits()
        {
            var planner = CreatePlanner(new FakeSelector(_ =>
            [
                CreateTile("p", "https://example.com/p.glb", depth: 0, parentTileId: null, hasChildren: false, span: 100d)
            ]));

            planner.Initialize(CreateRootTileset(), CreateRequest(dryRun: false));

            _ = planner.TryPlanNext(out PlannerCommand? firstItem).Should().BeTrue();
            UpdateLicenseCreditCommand initialCredit = firstItem.Should().BeOfType<UpdateLicenseCreditCommand>().Subject;
            _ = initialCredit.CreditString.Should().Be("Google Maps");
            planner.ApplyResult(new LicenseUpdatedResult(initialCredit.CreditString, true, null));

            _ = planner.TryPlanNext(out PlannerCommand? streamItem).Should().BeTrue();
            ProcessTileContentCommand stream = streamItem.Should().BeOfType<ProcessTileContentCommand>().Subject;
            planner.ApplyResult(new RenderableContentReadyResult(stream.Tile, 1, ["slot_1"], "Google; Airbus"));

            _ = planner.TryPlanNext(out PlannerCommand? secondItem).Should().BeTrue();
            UpdateLicenseCreditCommand secondCredit = secondItem.Should().BeOfType<UpdateLicenseCreditCommand>().Subject;
            _ = secondCredit.CreditString.Should().Be("Google; Airbus");
        }

        [Fact]
        public void MaxTiles_StopsAfterLimit()
        {
            var planner = CreatePlanner(new FakeSelector(_ =>
            [
                CreateTile("a", "https://example.com/a.glb", depth: 0, parentTileId: null, hasChildren: false, span: 100d),
                CreateTile("b", "https://example.com/b.glb", depth: 0, parentTileId: null, hasChildren: false, span: 90d)
            ]));

            planner.Initialize(CreateRootTileset(), CreateRequest(dryRun: true, maxTiles: 1));

            _ = planner.TryPlanNext(out PlannerCommand? firstItem).Should().BeTrue();
            ProcessTileContentCommand stream = firstItem.Should().BeOfType<ProcessTileContentCommand>().Subject;
            planner.ApplyResult(new RenderableContentReadyResult(stream.Tile, 1, [], null));

            _ = planner.TryPlanNext(out _).Should().BeFalse();

            RunSummary summary = planner.GetSummary();
            _ = summary.CandidateTiles.Should().Be(1);
            _ = summary.ProcessedTiles.Should().Be(1);
        }

        private static TraversalPlanner CreatePlanner(ITileSelector selector)
        {
            return new TraversalPlanner(selector, NullLogger<TraversalPlanner>.Instance);
        }

        private static TileRunRequest CreateRequest(bool dryRun, int maxTiles = 16, double bootstrapRangeMultiplier = 4d)
        {
            return new TileRunRequest(
                new GeoReference(0d, 0d, 0d),
                new TraversalOptions(500d, maxTiles, 16, 40d, bootstrapRangeMultiplier),
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
    }
}
