using System.Numerics;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Mesh
{
    internal sealed class MeshPlacementService(ICoordinateTransformer coordinateTransformer) : IMeshPlacementService
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
            string? parentNodeId)
        {
            ArgumentNullException.ThrowIfNull(tile);
            ArgumentNullException.ThrowIfNull(meshes);
            ArgumentNullException.ThrowIfNull(reference);

            var placed = new List<PlacedMeshPayload>(meshes.Count);
            foreach (MeshData mesh in meshes)
            {
                placed.Add(ToEunPayload(mesh, tile.WorldTransform, reference, tile.TileId, parentNodeId));
            }

            return placed;
        }

        private PlacedMeshPayload ToEunPayload(
            MeshData mesh,
            Matrix4x4d tileWorld,
            GeoReference reference,
            string tileId,
            string? parentNodeId)
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

            List<Vector3> generatedNormals = BuildVertexNormals(vertices, eunIndices);
            PayloadVectorTransform vectorTransform = CreatePayloadVectorTransform(basisXEun, basisYEun, basisZEun, invRotation, slotScale);
            bool flippedNormals = false;
            List<Vector3> normals = mesh.HasNormals
                ? TransformNormals(mesh.Normals!, vectorTransform, generatedNormals, out flippedNormals)
                : generatedNormals;

            List<Vector4>? tangents = mesh.HasTangents
                ? TransformTangents(mesh.Tangents!, vectorTransform, flippedNormals)
                : null;

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
                parentNodeId,
                normals,
                tangents);
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

        private static List<Vector3> BuildVertexNormals(List<Vector3> vertices, List<int> indices)
        {
            const float epsilon = 1e-12f;

            var accumulated = new Vector3[vertices.Count];
            Vector3 fallback = Vector3.UnitY;
            bool hasFallback = false;

            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                if ((uint)a >= (uint)vertices.Count || (uint)b >= (uint)vertices.Count || (uint)c >= (uint)vertices.Count)
                {
                    continue;
                }

                Vector3 ab = vertices[b] - vertices[a];
                Vector3 ac = vertices[c] - vertices[a];
                Vector3 face = Vector3.Cross(ab, ac);
                float lengthSquared = face.LengthSquared();
                if (lengthSquared <= epsilon)
                {
                    continue;
                }

                if (!hasFallback)
                {
                    fallback = Vector3.Normalize(face);
                    hasFallback = true;
                }

                accumulated[a] += face;
                accumulated[b] += face;
                accumulated[c] += face;
            }

            var normals = new List<Vector3>(vertices.Count);
            for (int i = 0; i < accumulated.Length; i++)
            {
                Vector3 normal = accumulated[i];
                normals.Add(normal.LengthSquared() <= epsilon ? fallback : Vector3.Normalize(normal));
            }

            return normals;
        }

        private static PayloadVectorTransform CreatePayloadVectorTransform(
            Vector3d basisXEun,
            Vector3d basisYEun,
            Vector3d basisZEun,
            Quaternion invRotation,
            Vector3 slotScale)
        {
            Vector3 x = TransformToLocalVector(basisXEun, invRotation, slotScale);
            Vector3 y = TransformToLocalVector(basisYEun, invRotation, slotScale);
            Vector3 z = TransformToLocalVector(basisZEun, invRotation, slotScale);
            float determinant = Vector3.Dot(x, Vector3.Cross(y, z));
            return new PayloadVectorTransform(x, y, z, determinant);
        }

        private static List<Vector3> TransformNormals(
            IReadOnlyList<Vector3d> normals,
            PayloadVectorTransform vectorTransform,
            IReadOnlyList<Vector3> generatedNormals,
            out bool flippedNormals)
        {
            var transformed = new List<Vector3>(normals.Count);
            foreach (Vector3d normal in normals)
            {
                transformed.Add(vectorTransform.TransformNormal(new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z), Vector3.UnitY));
            }

            flippedNormals = AlignNormalsToGeometry(transformed, generatedNormals);
            return transformed;
        }

        private static List<Vector4> TransformTangents(
            IReadOnlyList<Vector4> tangents,
            PayloadVectorTransform vectorTransform,
            bool flippedNormals)
        {
            float tangentWSign = vectorTransform.HandednessSign * (flippedNormals ? -1f : 1f);
            var transformed = new List<Vector4>(tangents.Count);
            foreach (Vector4 tangent in tangents)
            {
                Vector3 xyz = vectorTransform.TransformDirection(new Vector3(tangent.X, tangent.Y, tangent.Z), Vector3.UnitX);
                transformed.Add(new Vector4(xyz, tangent.W * tangentWSign));
            }

            return transformed;
        }

        private static bool AlignNormalsToGeometry(List<Vector3> transformedNormals, IReadOnlyList<Vector3> generatedNormals)
        {
            int count = System.Math.Min(transformedNormals.Count, generatedNormals.Count);
            float dotSum = 0f;
            for (int i = 0; i < count; i++)
            {
                dotSum += Vector3.Dot(transformedNormals[i], generatedNormals[i]);
            }

            if (dotSum >= 0f)
            {
                return false;
            }

            for (int i = 0; i < transformedNormals.Count; i++)
            {
                transformedNormals[i] = -transformedNormals[i];
            }

            return true;
        }

        private static Vector3 TransformToLocalVector(
            Vector3d eunDirection,
            Quaternion invRotation,
            Vector3 slotScale)
        {
            Vector3 localRotated = Vector3.Transform(
                new Vector3((float)eunDirection.X, (float)eunDirection.Y, (float)eunDirection.Z),
                invRotation);

            return new Vector3(
                slotScale.X > 1e-6f ? localRotated.X / slotScale.X : localRotated.X,
                slotScale.Y > 1e-6f ? localRotated.Y / slotScale.Y : localRotated.Y,
                slotScale.Z > 1e-6f ? localRotated.Z / slotScale.Z : localRotated.Z);
        }

        private readonly record struct PayloadVectorTransform(Vector3 X, Vector3 Y, Vector3 Z, float Determinant)
        {
            public float HandednessSign => Determinant < 0f ? -1f : 1f;

            public Vector3 TransformDirection(Vector3 value, Vector3 fallback)
            {
                Vector3 transformed = (X * value.X) + (Y * value.Y) + (Z * value.Z);
                return transformed.LengthSquared() <= 1e-12f ? fallback : Vector3.Normalize(transformed);
            }

            public Vector3 TransformNormal(Vector3 value, Vector3 fallback)
            {
                if (System.Math.Abs(Determinant) <= 1e-12f)
                {
                    return TransformDirection(value, fallback);
                }

                Vector3 transformed =
                    (Vector3.Cross(Y, Z) * value.X) +
                    (Vector3.Cross(Z, X) * value.Y) +
                    (Vector3.Cross(X, Y) * value.Z);

                transformed /= Determinant;
                return transformed.LengthSquared() <= 1e-12f ? fallback : Vector3.Normalize(transformed);
            }
        }
    }
}
