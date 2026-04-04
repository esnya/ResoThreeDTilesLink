using FluentAssertions;
using ThreeDTilesLink.Core.Geo;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests;

public sealed class TileSelectorTests
{
    [Fact]
    public void Select_HandlesRegionBoxSphereConservatively()
    {
        var transformer = new GeographicCoordinateTransformer();
        var selector = new TileSelector(transformer);

        var reference = new GeoReference(0d, 0d, 0d);
        var referenceEcef = transformer.GeographicToEcef(0d, 0d, 0d);

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

        var selected = selector.Select(tileset, reference, new QuerySquare(120d), maxDepth: 8, detailTargetM: 40d, maxTiles: 32, Matrix4x4d.Identity, string.Empty, 0);

        selected.Select(x => x.TileId).Should().Contain(new[] { "region-in", "box-in" });
        selected.Select(x => x.TileId).Should().NotContain("sphere-out");
    }
}
