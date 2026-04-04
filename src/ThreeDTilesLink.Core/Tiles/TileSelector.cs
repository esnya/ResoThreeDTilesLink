using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Tiles;

public sealed class TileSelector : ITileSelector
{
    private readonly ICoordinateTransformer _transformer;

    public TileSelector(ICoordinateTransformer transformer)
    {
        _transformer = transformer;
    }

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
        string? parentContentTileId)
    {
        _ = detailTargetM;
        var selectionLimit = maxTiles <= 0 ? int.MaxValue : maxTiles;
        var selected = new List<TileSelectionResult>(capacity: SMath.Max(1, SMath.Min(selectionLimit, 4096)));
        var queue = new Queue<(Tile Tile, Matrix4x4d ParentWorld, int Depth, string? ParentContentTileId)>();
        queue.Enqueue((tileset.Root, rootParentWorld, depthOffset, parentContentTileId));

        while (queue.Count > 0 && selected.Count < selectionLimit)
        {
            var (tile, parentWorld, depth, parentContentId) = queue.Dequeue();
            var local = tile.Transform is { Count: 16 }
                ? Matrix4x4d.FromCesiumColumnMajor(tile.Transform)
                : Matrix4x4d.Identity;
            var world = local * parentWorld;

            if (!Intersects(tile.BoundingVolume, world, reference, square, out var horizontalSpanM))
            {
                continue;
            }

            if (tile.ContentUri is not null)
            {
                var tileId = ComposeId(idPrefix, tile.Id);
                var hasChildren = tile.Children.Count > 0;
                var kind = DetectContentKind(tile.ContentUri);
                selected.Add(new TileSelectionResult(
                    tileId,
                    tile.ContentUri,
                    world,
                    depth,
                    parentContentId,
                    kind,
                    hasChildren,
                    horizontalSpanM,
                    []));
                parentContentId = tileId;
                if (selected.Count >= selectionLimit)
                {
                    break;
                }
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            // Breadth-first traversal: shallower tiles are discovered before deeper tiles.
            for (var i = 0; i < tile.Children.Count; i++)
            {
                queue.Enqueue((tile.Children[i], world, depth + 1, parentContentId));
            }
        }

        return selected;
    }

    private static TileContentKind DetectContentKind(Uri contentUri)
    {
        if (contentUri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return TileContentKind.Json;
        }

        if (contentUri.AbsolutePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            return TileContentKind.Glb;
        }

        return TileContentKind.Other;
    }

    private static string ComposeId(string prefix, string id)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return id.Replace("/", string.Empty, StringComparison.Ordinal);
        }

        return $"{prefix}{id}".Replace("/", string.Empty, StringComparison.Ordinal);
    }

    private bool Intersects(BoundingVolume? volume, Matrix4x4d world, GeoReference reference, QuerySquare square, out double? horizontalSpanM)
    {
        if (volume is null)
        {
            horizontalSpanM = null;
            return true;
        }

        if (!TryGetHorizontalBounds(volume, world, reference, out var minEast, out var maxEast, out var minNorth, out var maxNorth))
        {
            horizontalSpanM = null;
            return true;
        }

        horizontalSpanM = SMath.Max(maxEast - minEast, maxNorth - minNorth);
        return !(maxEast < square.Min || minEast > square.Max || maxNorth < square.Min || minNorth > square.Max);
    }

    private bool TryGetHorizontalBounds(
        BoundingVolume volume,
        Matrix4x4d world,
        GeoReference reference,
        out double minEast,
        out double maxEast,
        out double minNorth,
        out double maxNorth)
    {
        var eastValues = new List<double>();
        var northValues = new List<double>();

        if (volume.Region is { Count: 6 } region)
        {
            AppendRegionSamples(region, reference, eastValues, northValues);
        }

        if (volume.Box is { Count: 12 } box)
        {
            AppendBoxSamples(box, world, reference, eastValues, northValues);
        }

        if (volume.Sphere is { Count: 4 } sphere)
        {
            AppendSphereSamples(sphere, world, reference, eastValues, northValues);
        }

        if (eastValues.Count == 0 || northValues.Count == 0)
        {
            minEast = maxEast = minNorth = maxNorth = 0d;
            return false;
        }

        minEast = eastValues.Min();
        maxEast = eastValues.Max();
        minNorth = northValues.Min();
        maxNorth = northValues.Max();
        return true;
    }

    private void AppendRegionSamples(
        IReadOnlyList<double> region,
        GeoReference reference,
        ICollection<double> eastValues,
        ICollection<double> northValues)
    {
        var west = NormalizeLongitude(region[0], DegreesToRadians(reference.Longitude));
        var south = region[1];
        var east = NormalizeLongitude(region[2], DegreesToRadians(reference.Longitude));
        var north = region[3];

        if (east < west)
        {
            east += 2d * SMath.PI;
        }

        var minH = region[4];
        var maxH = region[5];

        foreach (var h in new[] { minH, maxH })
        {
            foreach (var lon in new[] { west, east })
            {
                foreach (var lat in new[] { south, north })
                {
                    var ecef = _transformer.GeographicToEcef(RadiansToDegrees(lat), RadiansToDegrees(lon), h);
                    var enu = _transformer.EcefToEnu(ecef, reference);
                    eastValues.Add(enu.X);
                    northValues.Add(enu.Y);
                }
            }
        }
    }

    private void AppendBoxSamples(
        IReadOnlyList<double> box,
        Matrix4x4d world,
        GeoReference reference,
        ICollection<double> eastValues,
        ICollection<double> northValues)
    {
        var center = world.TransformPoint(new Vector3d(box[0], box[1], box[2]));
        var axisX = world.TransformDirection(new Vector3d(box[3], box[4], box[5]));
        var axisY = world.TransformDirection(new Vector3d(box[6], box[7], box[8]));
        var axisZ = world.TransformDirection(new Vector3d(box[9], box[10], box[11]));

        foreach (var sx in new[] { -1d, 1d })
        {
            foreach (var sy in new[] { -1d, 1d })
            {
                foreach (var sz in new[] { -1d, 1d })
                {
                    var corner = center + (sx * axisX) + (sy * axisY) + (sz * axisZ);
                    var enu = _transformer.EcefToEnu(corner, reference);
                    eastValues.Add(enu.X);
                    northValues.Add(enu.Y);
                }
            }
        }
    }

    private void AppendSphereSamples(
        IReadOnlyList<double> sphere,
        Matrix4x4d world,
        GeoReference reference,
        ICollection<double> eastValues,
        ICollection<double> northValues)
    {
        var center = world.TransformPoint(new Vector3d(sphere[0], sphere[1], sphere[2]));
        var enu = _transformer.EcefToEnu(center, reference);
        var radius = SMath.Abs(sphere[3]) * world.MaxLinearScale();

        eastValues.Add(enu.X - radius);
        eastValues.Add(enu.X + radius);
        northValues.Add(enu.Y - radius);
        northValues.Add(enu.Y + radius);
    }

    private static double NormalizeLongitude(double lonRad, double aroundLonRad)
    {
        while ((lonRad - aroundLonRad) > SMath.PI)
        {
            lonRad -= 2d * SMath.PI;
        }

        while ((lonRad - aroundLonRad) < -SMath.PI)
        {
            lonRad += 2d * SMath.PI;
        }

        return lonRad;
    }

    private static double DegreesToRadians(double deg) => (SMath.PI / 180d) * deg;
    private static double RadiansToDegrees(double rad) => (180d / SMath.PI) * rad;
}
