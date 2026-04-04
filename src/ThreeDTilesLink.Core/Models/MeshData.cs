using ThreeDTilesLink.Core.Math;

namespace ThreeDTilesLink.Core.Models
{
    public sealed record MeshData(
        string Name,
        IReadOnlyList<Vector3d> Vertices,
        IReadOnlyList<int> Indices,
        IReadOnlyList<Vector2d> Uvs,
        bool HasUv0,
        Matrix4x4d LocalTransform,
        byte[]? BaseColorTextureBytes,
        string? BaseColorTextureExtension);

    public readonly record struct Vector2d(double X, double Y);
}
