using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Tiles
{
    internal sealed class TileSelector(ICoordinateTransformer transformer) : ITileSelector
    {
        private const double MaxBelowLocalPlaneM = 10000d;

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

            _ = CollectSelections(
                tileset.Root,
                rootParentWorld,
                depthOffset,
                parentContentTileId,
                parentContentStableId,
                idPrefix,
                reference,
                range,
                maxDepth,
                detailTargetM,
                selected,
                selectionLimit);

            return selected;
        }

        private SelectionOutcome CollectSelections(
            Tile tile,
            Matrix4x4d parentWorld,
            int depth,
            string? parentContentId,
            string? parentContentStableKey,
            string idPrefix,
            GeoReference reference,
            QueryRange range,
            int maxDepth,
            double detailTargetM,
            List<TileSelectionResult> selected,
            int selectionLimit)
        {
            if (selected.Count >= selectionLimit)
            {
                return new SelectionOutcome(false, HasAnyContent(tile));
            }

            Matrix4x4d local = tile.Transform is { Count: 16 }
                ? Matrix4x4d.FromCesiumColumnMajor(tile.Transform)
                : Matrix4x4d.Identity;
            Matrix4x4d world = local * parentWorld;

            if (!Intersects(tile.BoundingVolume, world, reference, range, out double? horizontalSpanM))
            {
                return new SelectionOutcome(false, HasAnyContent(tile));
            }

            TileSelectionResult? current = null;
            string? nextParentContentId = parentContentId;
            string? nextParentContentStableKey = parentContentStableKey;
            bool stopDescending = depth >= maxDepth || ShouldStopDescendingByDetail(tile, detailTargetM, horizontalSpanM);

            if (tile.ContentUri is not null)
            {
                string tileId = ComposeId(idPrefix, tile.Id);
                string stableId = ComposeStableId(idPrefix, tile.Id);
                TileContentKind kind = TileContentClassifier.Classify(tile.ContentUri);
                current = new TileSelectionResult(
                    tileId,
                    tile.ContentUri,
                    world,
                    depth,
                    parentContentId,
                    kind,
                    tile.Children.Count > 0,
                    horizontalSpanM,
                    stableId,
                    parentContentStableKey);
                nextParentContentId = tileId;
                nextParentContentStableKey = stableId;
            }

            bool hasSelectedDescendant = false;
            bool hasContentDescendant = false;
            if (!stopDescending)
            {
                for (int i = 0; i < tile.Children.Count; i++)
                {
                    SelectionOutcome childOutcome = CollectSelections(
                        tile.Children[i],
                        world,
                        depth + 1,
                        nextParentContentId,
                        nextParentContentStableKey,
                        idPrefix,
                        reference,
                        range,
                        maxDepth,
                        detailTargetM,
                        selected,
                        selectionLimit);
                    hasSelectedDescendant |= childOutcome.HasSelectedContent;
                    hasContentDescendant |= childOutcome.HasAnyContent;
                }
            }

            if (current is null)
            {
                return new SelectionOutcome(hasSelectedDescendant, hasContentDescendant);
            }

            bool keepCurrent = current.ContentKind == TileContentKind.Json ||
                !current.HasChildren ||
                stopDescending ||
                hasSelectedDescendant ||
                !hasContentDescendant;

            if (keepCurrent && selected.Count < selectionLimit)
            {
                selected.Add(current);
                return new SelectionOutcome(true, true);
            }

            return new SelectionOutcome(hasSelectedDescendant, true);
        }

        private static bool HasAnyContent(Tile tile)
        {
            if (tile.ContentUri is not null)
            {
                return true;
            }

            for (int i = 0; i < tile.Children.Count; i++)
            {
                if (HasAnyContent(tile.Children[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly record struct SelectionOutcome(bool HasSelectedContent, bool HasAnyContent);

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

            // Relay nodes should keep descending so descendants can provide renderable content.
            return TileContentClassifier.Classify(tile.ContentUri) != TileContentKind.Json;
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

            if (!TryGetLocalBounds(
                    volume,
                    world,
                    reference,
                    out double minEast,
                    out double maxEast,
                    out double minNorth,
                    out double maxNorth,
                    out double _,
                    out double maxUp))
            {
                horizontalSpanM = null;
                return true;
            }

            horizontalSpanM = SMath.Max(maxEast - minEast, maxNorth - minNorth);
            if (maxUp < -MaxBelowLocalPlaneM)
            {
                return false;
            }

            return !(maxEast < range.Min || minEast > range.Max || maxNorth < range.Min || minNorth > range.Max);
        }

        private bool TryGetLocalBounds(
            BoundingVolume volume,
            Matrix4x4d world,
            GeoReference reference,
            out double minEast,
            out double maxEast,
            out double minNorth,
            out double maxNorth,
            out double minUp,
            out double maxUp)
        {
            var eastValues = new List<double>();
            var northValues = new List<double>();
            var upValues = new List<double>();

            if (volume.Region is { Count: 6 } region)
            {
                AppendRegionSamples(region, reference, eastValues, northValues, upValues);
            }

            if (volume.Box is { Count: 12 } box)
            {
                AppendBoxSamples(box, world, reference, eastValues, northValues, upValues);
            }

            if (volume.Sphere is { Count: 4 } sphere)
            {
                AppendSphereSamples(sphere, world, reference, eastValues, northValues, upValues);
            }

            if (eastValues.Count == 0 || northValues.Count == 0 || upValues.Count == 0)
            {
                minEast = maxEast = minNorth = maxNorth = minUp = maxUp = 0d;
                return false;
            }

            minEast = eastValues.Min();
            maxEast = eastValues.Max();
            minNorth = northValues.Min();
            maxNorth = northValues.Max();
            minUp = upValues.Min();
            maxUp = upValues.Max();
            return true;
        }

        private void AppendRegionSamples(
            IReadOnlyList<double> region,
            GeoReference reference,
            List<double> eastValues,
            List<double> northValues,
            List<double> upValues)
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
                        upValues.Add(enu.Z);
                    }
                }
            }
        }

        private void AppendBoxSamples(
            IReadOnlyList<double> box,
            Matrix4x4d world,
            GeoReference reference,
            List<double> eastValues,
            List<double> northValues,
            List<double> upValues)
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
                        upValues.Add(enu.Z);
                    }
                }
            }
        }

        private void AppendSphereSamples(
            IReadOnlyList<double> sphere,
            Matrix4x4d world,
            GeoReference reference,
            List<double> eastValues,
            List<double> northValues,
            List<double> upValues)
        {
            Vector3d center = world.TransformPoint(new Vector3d(sphere[0], sphere[1], sphere[2]));
            Vector3d enu = _transformer.EcefToEnu(center, reference);
            double radius = SMath.Abs(sphere[3]) * world.MaxLinearScale();

            eastValues.Add(enu.X - radius);
            eastValues.Add(enu.X + radius);
            northValues.Add(enu.Y - radius);
            northValues.Add(enu.Y + radius);
            upValues.Add(enu.Z - radius);
            upValues.Add(enu.Z + radius);
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
