using FluentAssertions;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class TileSelectorTests
    {
        [Fact]
        public void Select_HandlesRegionBoxSphereConservatively()
        {
            var transformer = new GeographicCoordinateTransformer();
            var selector = new TileSelector(transformer);

            var reference = new GeoReference(0d, 0d, 0d);
            Vector3d referenceEcef = transformer.GeographicToEcef(0d, 0d, 0d);

            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "region-in",
                        ContentUri = new Uri("https://example.com/region-in.glb"),
                        BoundingVolume = new BoundingVolume
                        {
                            Region = [-0.00002, -0.00002, 0.00002, 0.00002, -10d, 100d]
                        }
                    },
                    new Tile
                    {
                        Id = "box-in",
                        ContentUri = new Uri("https://example.com/box-in.glb"),
                        BoundingVolume = new BoundingVolume
                        {
                            Box =
                            [
                                referenceEcef.X, referenceEcef.Y, referenceEcef.Z,
                                0d, 40d, 0d,
                                0d, 0d, 40d,
                                40d, 0d, 0d
                            ]
                        }
                    },
                    new Tile
                    {
                        Id = "sphere-out",
                        ContentUri = new Uri("https://example.com/sphere-out.glb"),
                        BoundingVolume = new BoundingVolume
                        {
                            Sphere = [referenceEcef.X, referenceEcef.Y + 5000d, referenceEcef.Z, 30d]
                        }
                    }
                ]
            });

            IReadOnlyList<TileSelectionResult> selected = selector.Select(tileset, reference, new QuerySquare(120d), maxDepth: 8, detailTargetM: 40d, maxTiles: 32, Matrix4x4d.Identity, string.Empty, 0, null, null);

            _ = selected.Select(x => x.TileId).Should().Contain(["region-in", "box-in"]);
            _ = selected.Select(x => x.TileId).Should().NotContain("sphere-out");
        }

        [Fact]
        public void Select_ReturnsAllIntersectedContentTiles_WithParentAndKindMetadata()
        {
            var selector = new TileSelector(new GeographicCoordinateTransformer());

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
                            },
                            new Tile
                            {
                                Id = "j",
                                ContentUri = new Uri("https://example.com/nested.json")
                            }
                        ]
                    }
                ]
            });

            IReadOnlyList<TileSelectionResult> selected = selector.Select(
                tileset,
                new GeoReference(0d, 0d, 0d),
                new QuerySquare(500d),
                maxDepth: 16,
                detailTargetM: 30d,
                maxTiles: 64,
                Matrix4x4d.Identity,
                string.Empty,
                0,
                null,
                null);

            _ = selected.Select(x => x.TileId).Should().Contain(["p", "c", "j"]);
            _ = selected.Should().ContainSingle(x => x.TileId == "p" && x.ParentTileId == null && x.ContentKind == TileContentKind.Glb);
            _ = selected.Should().ContainSingle(x => x.TileId == "c" && x.ParentTileId == "p" && x.ContentKind == TileContentKind.Glb);
            _ = selected.Should().ContainSingle(x => x.TileId == "j" && x.ParentTileId == "p" && x.ContentKind == TileContentKind.Json);
        }

        [Fact]
        public void Select_StopsDescendingGlb_WhenDetailTargetReached_ButKeepsJsonRelayTraversal()
        {
            var transformer = new GeographicCoordinateTransformer();
            var selector = new TileSelector(transformer);
            var reference = new GeoReference(0d, 0d, 0d);
            Vector3d center = transformer.GeographicToEcef(0d, 0d, 0d);

            static BoundingVolume BoxAt(Vector3d centerEcef, double halfExtentM)
            {
                return new BoundingVolume
                {
                    Box =
                    [
                        centerEcef.X, centerEcef.Y, centerEcef.Z,
                        0d, halfExtentM, 0d,
                        0d, 0d, halfExtentM,
                        halfExtentM, 0d, 0d
                    ]
                };
            }

            var tileset = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "g0",
                        ContentUri = new Uri("https://example.com/g0.glb"),
                        BoundingVolume = BoxAt(center, 120d),
                        Children =
                        [
                            new Tile
                            {
                                Id = "g1",
                                ContentUri = new Uri("https://example.com/g1.glb"),
                                BoundingVolume = BoxAt(center, 20d),
                                Children =
                                [
                                    new Tile
                                    {
                                        Id = "g2",
                                        ContentUri = new Uri("https://example.com/g2.glb"),
                                        BoundingVolume = BoxAt(center, 5d)
                                    }
                                ]
                            }
                        ]
                    },
                    new Tile
                    {
                        Id = "j0",
                        ContentUri = new Uri("https://example.com/j0.json"),
                        BoundingVolume = BoxAt(center, 20d),
                        Children =
                        [
                            new Tile
                            {
                                Id = "j1",
                                ContentUri = new Uri("https://example.com/j1.glb"),
                                BoundingVolume = BoxAt(center, 5d)
                            }
                        ]
                    }
                ]
            });

            IReadOnlyList<TileSelectionResult> selected = selector.Select(
                tileset,
                reference,
                new QuerySquare(500d),
                maxDepth: 16,
                detailTargetM: 60d,
                maxTiles: 64,
                Matrix4x4d.Identity,
                string.Empty,
                0,
                null,
                null);

            _ = selected.Select(x => x.TileId).Should().Contain(["g0", "g1", "j0", "j1"]);
            _ = selected.Select(x => x.TileId).Should().NotContain("g2");
        }

        [Fact]
        public void Select_ComposeId_KeepsCompactReadableIds()
        {
            var selector = new TileSelector(new GeographicCoordinateTransformer());
            var reference = new GeoReference(0d, 0d, 0d);
            var square = new QuerySquare(500d);

            var tilesetA = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "3",
                        ContentUri = new Uri("https://example.com/a.glb")
                    }
                ]
            });

            var tilesetB = new Tileset(new Tile
            {
                Id = "root",
                Children =
                [
                    new Tile
                    {
                        Id = "23",
                        ContentUri = new Uri("https://example.com/b.glb")
                    }
                ]
            });

            IReadOnlyList<TileSelectionResult> selectedA = selector.Select(
                tilesetA,
                reference,
                square,
                maxDepth: 8,
                detailTargetM: 40d,
                maxTiles: 32,
                Matrix4x4d.Identity,
                idPrefix: "12",
                depthOffset: 0,
                parentContentTileId: "12",
                parentContentStableId: "stable12");

            IReadOnlyList<TileSelectionResult> selectedB = selector.Select(
                tilesetB,
                reference,
                square,
                maxDepth: 8,
                detailTargetM: 40d,
                maxTiles: 32,
                Matrix4x4d.Identity,
                idPrefix: "1",
                depthOffset: 0,
                parentContentTileId: "1",
                parentContentStableId: "stable1");

            string idA = selectedA.Should().ContainSingle().Subject.TileId;
            string idB = selectedB.Should().ContainSingle().Subject.TileId;
            _ = idA.Should().Be("123");
            _ = idB.Should().Be("123");
            string stableA = selectedA.Single().StableId!;
            string stableB = selectedB.Single().StableId!;
            _ = stableA.Should().NotBe(stableB);
        }
    }
}
