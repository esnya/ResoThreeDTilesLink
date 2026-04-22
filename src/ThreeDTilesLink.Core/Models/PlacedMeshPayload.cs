using System.Numerics;

namespace ThreeDTilesLink.Core.Models
{
    internal sealed record PlacedMeshPayload(
        string Name,
        IReadOnlyList<Vector3> Vertices,
        IReadOnlyList<int> Indices,
        IReadOnlyList<Vector2> Uvs,
        bool HasUv0,
        Vector3 SlotPosition,
        Quaternion SlotRotation,
        Vector3 SlotScale,
        byte[]? BaseColorTextureBytes,
        string? BaseColorTextureExtension,
        string? ParentNodeId = null,
        IReadOnlyList<Vector3>? Normals = null,
        IReadOnlyList<Vector4>? Tangents = null)
    {
        public bool HasNormals => Normals is { Count: > 0 };
        public bool HasTangents => Tangents is { Count: > 0 };
    }
}
