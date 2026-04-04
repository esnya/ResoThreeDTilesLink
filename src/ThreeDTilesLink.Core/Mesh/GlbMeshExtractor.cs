using System.Numerics;
using SharpGLTF.Schema2;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Mesh;

public sealed class GlbMeshExtractor : IGlbMeshExtractor
{
    public GlbExtractResult Extract(byte[] glbBytes)
    {
        using var stream = new MemoryStream(glbBytes, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        var scene = model.DefaultScene ?? model.LogicalScenes.FirstOrDefault();
        if (scene is null)
        {
            return new GlbExtractResult(Array.Empty<MeshData>(), model.Asset?.Copyright);
        }

        var result = new List<MeshData>();

        foreach (var node in scene.VisualChildren)
        {
            ExtractNode(node, result);
        }

        return new GlbExtractResult(result, model.Asset?.Copyright);
    }

    private static void ExtractNode(Node node, ICollection<MeshData> output)
    {
        if (node.Mesh is not null)
        {
            var nodeMatrix = node.WorldMatrix;
            foreach (var primitive in node.Mesh.Primitives)
            {
                var positionAccessor = primitive.GetVertexAccessor("POSITION");
                if (positionAccessor is null)
                {
                    continue;
                }

                var positions = positionAccessor.AsVector3Array();
                var vertices = new List<Vector3d>(positions.Count);
                for (var i = 0; i < positions.Count; i++)
                {
                    Vector3 p = positions[i];
                    vertices.Add(new Vector3d(p.X, p.Y, p.Z));
                }

                var indices = new List<int>();
                foreach (var (a, b, c) in primitive.GetTriangleIndices())
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                }

                var baseColorChannel = primitive.Material?.FindChannel("BaseColor");
                var textureCoordSet = System.Math.Max(0, baseColorChannel?.TextureCoordinate ?? 0);
                var uvSemantic = $"TEXCOORD_{textureCoordSet}";
                var uvAccessor = primitive.GetVertexAccessor(uvSemantic) ?? primitive.GetVertexAccessor("TEXCOORD_0");
                var uvVectors = uvAccessor?.AsVector2Array();
                var hasUv0 = uvAccessor is not null;
                var uvs = new List<Vector2d>(hasUv0 ? positions.Count : 0);
                if (hasUv0)
                {
                    for (var i = 0; i < positions.Count; i++)
                    {
                        if (uvVectors is not null && i < uvVectors.Count)
                        {
                            var uv = uvVectors[i];
                            // glTF 2.0 defines UV (0,0) at image upper-left.
                            // Resonite mesh sampling uses the opposite V direction, so normalize at import boundary.
                            // Keep image bytes untouched and convert UV only.
                            uvs.Add(new Vector2d(uv.X, 1d - uv.Y));
                        }
                        else
                        {
                            uvs.Add(new Vector2d(0d, 0d));
                        }
                    }
                }

                var texture = baseColorChannel?.Texture;
                var image = SelectTextureImage(texture);
                var textureBytes = image is { Content.IsEmpty: false } ? image.Content.Content.ToArray() : null;
                var textureExtension = ResolveImageExtension(image);

                if (vertices.Count > 0 && indices.Count > 0)
                {
                    var name = string.IsNullOrWhiteSpace(node.Name)
                        ? $"mesh_{primitive.LogicalIndex}"
                        : $"{node.Name}_{primitive.LogicalIndex}";
                    output.Add(new MeshData(
                        name,
                        vertices,
                        indices,
                        uvs,
                        hasUv0,
                        Matrix4x4d.FromNumerics(nodeMatrix),
                        textureBytes,
                        textureExtension));
                }
            }
        }

        foreach (var child in node.VisualChildren)
        {
            ExtractNode(child, output);
        }
    }

    private static Image? SelectTextureImage(Texture? texture)
    {
        if (texture is null)
        {
            return null;
        }

        if (texture.PrimaryImage is { Content.IsEmpty: false } primary)
        {
            return primary;
        }

        if (texture.FallbackImage is { Content.IsEmpty: false } fallback)
        {
            return fallback;
        }

        return texture.PrimaryImage ?? texture.FallbackImage;
    }

    private static string? ResolveImageExtension(Image? image)
    {
        if (image is null)
        {
            return ".png";
        }

        if (!string.IsNullOrWhiteSpace(image.Content.FileExtension))
        {
            if (image.Content.FileExtension.StartsWith(".", StringComparison.Ordinal))
            {
                return image.Content.FileExtension;
            }

            return $".{image.Content.FileExtension}";
        }

        if (!string.IsNullOrWhiteSpace(image.Name))
        {
            var extFromName = Path.GetExtension(image.Name);
            if (!string.IsNullOrWhiteSpace(extFromName))
            {
                return extFromName;
            }
        }

        return image.Content.MimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png"
        };
    }
}
