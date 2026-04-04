using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Tiles
{
    public sealed class TileSelector(ICoordinateTransformer transformer) : ITileSelector
    {
        private readonly ICoordinateTransformer _transformer = transformer;

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
            ArgumentNullException.ThrowIfNull(tileset);
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentNullException.ThrowIfNull(range);
            int selectionLimit = maxTiles <= 0 ? int.MaxValue : maxTiles;
            var selected = new List<TileSelectionResult>(capacity: SMath.Max(1, SMath.Min(selectionLimit, 4096)));
            var queue = new Queue<(Tile Tile, Matrix4x4d ParentWorld, int Depth, string? ParentContentTileId, string? ParentContentStableId)>();
            queue.Enqueue((tileset.Root, rootParentWorld, depthOffset, parentContentTileId, parentContentStableId));

            while (queue.Count > 0 && selected.Count < selectionLimit)
            {
                (Tile? tile, Matrix4x4d parentWorld, int depth, string? parentContentId, string? parentContentStableKey) = queue.Dequeue();
                Matrix4x4d local = tile.Transform is { Count: 16 }
                    ? Matrix4x4d.FromCesiumColumnMajor(tile.Transform)
                    : Matrix4x4d.Identity;
                Matrix4x4d world = local * parentWorld;

                if (!Intersects(tile.BoundingVolume, world, reference, range, out double? horizontalSpanM))
                {
                    continue;
                }

                if (tile.ContentUri is not null)
                {
                    string tileId = ComposeId(idPrefix, tile.Id);
                    string stableId = ComposeStableId(idPrefix, tile.Id);
                    bool hasChildren = tile.Children.Count > 0;
                    TileContentKind kind = DetectContentKind(tile.ContentUri);
                    selected.Add(new TileSelectionResult(
                        tileId,
                        tile.ContentUri,
                        world,
                        depth,
                        parentContentId,
                        kind,
                        hasChildren,
                        horizontalSpanM,
                        [],
                        stableId,
                        parentContentStableKey));
                    parentContentId = tileId;
                    parentContentStableKey = stableId;
                    if (selected.Count >= selectionLimit)
                    {
                        break;
                    }
                }

                if (depth >= maxDepth || ShouldStopDescendingByDetail(tile, detailTargetM, horizontalSpanM))
                {
                    continue;
                }

                // Breadth-first traversal: shallower tiles are discovered before deeper tiles.
                for (int i = 0; i < tile.Children.Count; i++)
                {
                    queue.Enqueue((tile.Children[i], world, depth + 1, parentContentId, parentContentStableKey));
                }
            }

            return selected;
        }

        private static bool ShouldStopDescendingByDetail(Tile tile, double detailTargetM, double? horizontalSpanM)
        {
            if (detailTargetM <= 0d || horizontalSpanM is null || horizontalSpanM.Value > detailTargetM)
            {
                return false;
            }

            if (tile.ContentUri is null)
            {
                return false;
            }

            // JSON tiles are relay nodes; keep traversing so descendants can provide renderable GLBs.
            return DetectContentKind(tile.ContentUri) != TileContentKind.Json;
        }

        private static TileContentKind DetectContentKind(Uri contentUri)
        {
            return contentUri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? TileContentKind.Json
                : contentUri.AbsolutePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ? TileContentKind.Glb : TileContentKind.Other;
        }

        private static string ComposeId(string prefix, string id)
        {
            string segment = id.Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                segment = "tile";
            }

            return string.IsNullOrWhiteSpace(prefix)
                ? segment
                : $"{prefix}{segment}";
        }

        private static string ComposeStableId(string prefix, string id)
        {
            string normalizedPrefix = prefix ?? string.Empty;
            string normalizedId = id ?? string.Empty;
            return $"{normalizedPrefix.Length}:{normalizedPrefix}|{normalizedId.Length}:{normalizedId}";
        }

        private bool Intersects(BoundingVolume? volume, Matrix4x4d world, GeoReference reference, QueryRange range, out double? horizontalSpanM)
        {
            if (volume is null)
            {
                horizontalSpanM = null;
                return true;
            }

            if (!TryGetHorizontalBounds(volume, world, reference, out double minEast, out double maxEast, out double minNorth, out double maxNorth))
            {
                horizontalSpanM = null;
                return true;
            }

            horizontalSpanM = SMath.Max(maxEast - minEast, maxNorth - minNorth);
            return !(maxEast < range.Min || minEast > range.Max || maxNorth < range.Min || minNorth > range.Max);
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
            double west = NormalizeLongitude(region[0], DegreesToRadians(reference.Longitude));
            double south = region[1];
            double east = NormalizeLongitude(region[2], DegreesToRadians(reference.Longitude));
            double north = region[3];

            if (east < west)
            {
                east += 2d * SMath.PI;
            }

            double minH = region[4];
            double maxH = region[5];

            foreach (double h in new[] { minH, maxH })
            {
                foreach (double lon in new[] { west, east })
                {
                    foreach (double lat in new[] { south, north })
                    {
                        Vector3d ecef = _transformer.GeographicToEcef(RadiansToDegrees(lat), RadiansToDegrees(lon), h);
                        Vector3d enu = _transformer.EcefToEnu(ecef, reference);
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
            Vector3d center = world.TransformPoint(new Vector3d(box[0], box[1], box[2]));
            Vector3d axisX = world.TransformDirection(new Vector3d(box[3], box[4], box[5]));
            Vector3d axisY = world.TransformDirection(new Vector3d(box[6], box[7], box[8]));
            Vector3d axisZ = world.TransformDirection(new Vector3d(box[9], box[10], box[11]));

            foreach (double sx in new[] { -1d, 1d })
            {
                foreach (double sy in new[] { -1d, 1d })
                {
                    foreach (double sz in new[] { -1d, 1d })
                    {
                        Vector3d corner = center + (sx * axisX) + (sy * axisY) + (sz * axisZ);
                        Vector3d enu = _transformer.EcefToEnu(corner, reference);
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
            Vector3d center = world.TransformPoint(new Vector3d(sphere[0], sphere[1], sphere[2]));
            Vector3d enu = _transformer.EcefToEnu(center, reference);
            double radius = SMath.Abs(sphere[3]) * world.MaxLinearScale();

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

        private static double DegreesToRadians(double deg)
        {
            return SMath.PI / 180d * deg;
        }

        private static double RadiansToDegrees(double rad)
        {
            return 180d / SMath.PI * rad;
        }
    }
}
