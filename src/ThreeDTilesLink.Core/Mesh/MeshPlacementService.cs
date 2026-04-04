using System.Numerics;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Mesh
{
    public sealed class MeshPlacementService(ICoordinateTransformer coordinateTransformer) : IMeshPlacementService
    {
        // 3D Tiles glTF content is Y-up; convert to tiles/world Z-up before tile transform application.
        private static readonly Matrix4x4d GltfYUpToZUp = new(
            1d, 0d, 0d, 0d,
            0d, 0d, 1d, 0d,
            0d, -1d, 0d, 0d,
            0d, 0d, 0d, 1d);

        private readonly ICoordinateTransformer _coordinateTransformer = coordinateTransformer;

        public IReadOnlyList<PlacedMeshPayload> Place(
            TileSelectionResult tile,
            IReadOnlyList<MeshData> meshes,
            GeoReference reference,
            string? parentSlotId)
        {
            ArgumentNullException.ThrowIfNull(tile);
            ArgumentNullException.ThrowIfNull(meshes);
            ArgumentNullException.ThrowIfNull(reference);

            var placed = new List<PlacedMeshPayload>(meshes.Count);
            foreach (MeshData mesh in meshes)
            {
                placed.Add(ToEunPayload(mesh, tile.WorldTransform, reference, tile.TileId, parentSlotId));
            }

            return placed;
        }

        private PlacedMeshPayload ToEunPayload(
            MeshData mesh,
            Matrix4x4d tileWorld,
            GeoReference reference,
            string tileId,
            string? parentSlotId)
        {
            Matrix4x4d meshWorld = mesh.LocalTransform * GltfYUpToZUp * tileWorld;
            Vector3d meshOriginEcef = meshWorld.TransformPoint(new Vector3d(0d, 0d, 0d));
            Vector3d meshOriginEun = ToEun(meshOriginEcef, reference);

            Vector3d basisXEun = ToEun(meshWorld.TransformPoint(new Vector3d(1d, 0d, 0d)), reference) - meshOriginEun;
            Vector3d basisYEun = ToEun(meshWorld.TransformPoint(new Vector3d(0d, 1d, 0d)), reference) - meshOriginEun;
            Vector3d basisZEun = ToEun(meshWorld.TransformPoint(new Vector3d(0d, 0d, 1d)), reference) - meshOriginEun;
            (Quaternion slotRotation, Vector3 slotScale) = BuildSlotFrame(basisXEun, basisYEun, basisZEun);
            Quaternion invRotation = Quaternion.Inverse(slotRotation);

            var worldVertices = new List<Vector3d>(mesh.Vertices.Count);
            var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            var max = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
            foreach (Vector3d vertex in mesh.Vertices)
            {
                Vector3d worldEcef = meshWorld.TransformPoint(vertex);
                Vector3d worldEun = ToEun(worldEcef, reference);
                worldVertices.Add(worldEun);

                min = new Vector3d(
                    SMath.Min(min.X, worldEun.X),
                    SMath.Min(min.Y, worldEun.Y),
                    SMath.Min(min.Z, worldEun.Z));
                max = new Vector3d(
                    SMath.Max(max.X, worldEun.X),
                    SMath.Max(max.Y, worldEun.Y),
                    SMath.Max(max.Z, worldEun.Z));
            }

            var slotOriginEun = new Vector3d(
                (min.X + max.X) * 0.5d,
                (min.Y + max.Y) * 0.5d,
                (min.Z + max.Z) * 0.5d);

            var vertices = new List<Vector3>(worldVertices.Count);
            foreach (Vector3d worldEun in worldVertices)
            {
                Vector3d delta = worldEun - slotOriginEun;
                Vector3 localRotated = Vector3.Transform(
                    new Vector3((float)delta.X, (float)delta.Y, (float)delta.Z),
                    invRotation);

                vertices.Add(new Vector3(
                    slotScale.X > 1e-6f ? localRotated.X / slotScale.X : localRotated.X,
                    slotScale.Y > 1e-6f ? localRotated.Y / slotScale.Y : localRotated.Y,
                    slotScale.Z > 1e-6f ? localRotated.Z / slotScale.Z : localRotated.Z));
            }

            var uvs = new List<Vector2>(mesh.Uvs.Count);
            foreach (Vector2d uv in mesh.Uvs)
            {
                uvs.Add(new Vector2((float)uv.X, (float)uv.Y));
            }

            var eunIndices = new List<int>(mesh.Indices.Count);
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int a = mesh.Indices[i];
                int b = mesh.Indices[i + 1];
                int c = mesh.Indices[i + 2];
                eunIndices.Add(a);
                eunIndices.Add(c);
                eunIndices.Add(b);
            }

            return new PlacedMeshPayload(
                BuildMeshSlotName(tileId, mesh.Name),
                vertices,
                eunIndices,
                uvs,
                mesh.HasUv0,
                new Vector3((float)slotOriginEun.X, (float)slotOriginEun.Y, (float)slotOriginEun.Z),
                slotRotation,
                slotScale,
                mesh.BaseColorTextureBytes,
                mesh.BaseColorTextureExtension,
                parentSlotId);
        }

        private Vector3d ToEun(Vector3d ecef, GeoReference reference)
        {
            Vector3d enu = _coordinateTransformer.EcefToEnu(ecef, reference);
            return _coordinateTransformer.EnuToEun(enu);
        }

        private static string BuildMeshSlotName(string tileId, string meshName)
        {
            string compactTileId = tileId.Replace("/", string.Empty, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(compactTileId))
            {
                compactTileId = "tile";
            }

            return $"tile_{compactTileId}_{meshName}";
        }

        private static (Quaternion Rotation, Vector3 Scale) BuildSlotFrame(Vector3d basisX, Vector3d basisY, Vector3d basisZ)
        {
            const double epsilon = 1e-9d;
            float sx = (float)SMath.Max(basisX.Length(), epsilon);
            float sy = (float)SMath.Max(basisY.Length(), epsilon);
            float sz = (float)SMath.Max(basisZ.Length(), epsilon);

            Vector3d x = NormalizeOrFallback(basisX, new Vector3d(1d, 0d, 0d));
            Vector3d yProjected = basisY - (Vector3d.Dot(basisY, x) * x);
            Vector3d y = NormalizeOrFallback(yProjected, new Vector3d(0d, 1d, 0d));
            Vector3d z = Vector3d.Normalize(Vector3d.Cross(x, y));

            if (z.Length() <= epsilon)
            {
                z = NormalizeOrFallback(basisZ, new Vector3d(0d, 0d, 1d));
                y = NormalizeOrFallback(Vector3d.Cross(z, x), new Vector3d(0d, 1d, 0d));
                z = NormalizeOrFallback(Vector3d.Cross(x, y), new Vector3d(0d, 0d, 1d));
            }

            if (Vector3d.Dot(z, basisZ) < 0d)
            {
                y = -1d * y;
                z = -1d * z;
            }

            var rotationMatrix = new Matrix4x4(
                (float)x.X, (float)x.Y, (float)x.Z, 0f,
                (float)y.X, (float)y.Y, (float)y.Z, 0f,
                (float)z.X, (float)z.Y, (float)z.Z, 0f,
                0f, 0f, 0f, 1f);

            Quaternion rotation = Quaternion.CreateFromRotationMatrix(rotationMatrix);
            rotation = !float.IsFinite(rotation.X) ||
                !float.IsFinite(rotation.Y) ||
                !float.IsFinite(rotation.Z) ||
                !float.IsFinite(rotation.W)
                ? Quaternion.Identity
                : Quaternion.Normalize(rotation);

            return (rotation, new Vector3(sx, sy, sz));
        }

        private static Vector3d NormalizeOrFallback(Vector3d value, Vector3d fallback)
        {
            Vector3d normalized = Vector3d.Normalize(value);
            return normalized.Length() <= 1e-9d ? fallback : normalized;
        }
    }
}
