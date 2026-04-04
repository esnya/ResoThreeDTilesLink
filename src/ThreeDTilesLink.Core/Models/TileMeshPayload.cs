using System.Numerics;

namespace ThreeDTilesLink.Core.Models;

public sealed record TileMeshPayload(
    string Name,
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<int> Indices,
    IReadOnlyList<Vector2> Uvs,
    bool HasUv0,
    Vector3 SlotPosition,
    Quaternion SlotRotation,
    Vector3 SlotScale,
    byte[]? BaseColorTextureBytes,
    string? BaseColorTextureExtension);
