using System.Numerics;
using ThreeDTilesLink.Core.Math;

namespace ThreeDTilesLink.Core.Models
{
    internal sealed record MeshData(
        string Name,
        IReadOnlyList<Vector3d> Vertices,
        IReadOnlyList<int> Indices,
        IReadOnlyList<Vector2d> Uvs,
        bool HasUv0,
        IReadOnlyList<Vector3d>? Normals,
        IReadOnlyList<Vector4>? Tangents,
        Matrix4x4d LocalTransform,
        byte[]? BaseColorTextureBytes,
        string? BaseColorTextureExtension)
    {
        public bool HasNormals => Normals is { Count: > 0 };
        public bool HasTangents => Tangents is { Count: > 0 };
    }

    internal readonly record struct Vector2d(double X, double Y);
}
