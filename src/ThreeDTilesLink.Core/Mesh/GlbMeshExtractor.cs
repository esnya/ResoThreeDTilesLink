using System.Numerics;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Mesh
{
    internal sealed class GlbMeshExtractor : IGlbMeshExtractor
    {
        public GlbExtractResult Extract(byte[] glbBytes)
        {
            using var stream = new MemoryStream(glbBytes, writable: false);
            var model = ModelRoot.ReadGLB(stream);
            Scene? scene = model.DefaultScene;
            if (scene is null && model.LogicalScenes.Count > 0)
            {
                scene = model.LogicalScenes[0];
            }
            if (scene is null)
            {
                return new GlbExtractResult([], model.Asset?.Copyright);
            }

            var result = new List<MeshData>();

            foreach (Node? node in scene.VisualChildren)
            {
                ExtractNode(node, result);
            }

            return new GlbExtractResult(result, model.Asset?.Copyright);
        }

        private static void ExtractNode(Node node, List<MeshData> output)
        {
            if (node.Mesh is not null)
            {
                Matrix4x4 nodeMatrix = node.WorldMatrix;
                foreach (MeshPrimitive? primitive in node.Mesh.Primitives)
                {
                    Accessor? positionAccessor = primitive.GetVertexAccessor("POSITION");
                    if (positionAccessor is null)
                    {
                        continue;
                    }

                    IAccessorArray<Vector3> positions = positionAccessor.AsVector3Array();
                    var vertices = new List<Vector3d>(positions.Count);
                    for (int i = 0; i < positions.Count; i++)
                    {
                        Vector3 p = positions[i];
                        vertices.Add(new Vector3d(p.X, p.Y, p.Z));
                    }

                    var indices = new List<int>();
                    foreach ((int a, int b, int c) in primitive.GetTriangleIndices())
                    {
                        indices.Add(a);
                        indices.Add(b);
                        indices.Add(c);
                    }

                    MaterialChannel? baseColorChannel = primitive.Material?.FindChannel("BaseColor");
                    int textureCoordSet = System.Math.Max(0, baseColorChannel?.TextureCoordinate ?? 0);
                    string uvSemantic = $"TEXCOORD_{textureCoordSet}";
                    Accessor? uvAccessor = primitive.GetVertexAccessor(uvSemantic) ?? primitive.GetVertexAccessor("TEXCOORD_0");
                    IAccessorArray<Vector2>? uvVectors = uvAccessor?.AsVector2Array();
                    bool hasUv0 = uvAccessor is not null;
                    var uvs = new List<Vector2d>(hasUv0 ? positions.Count : 0);
                    if (hasUv0)
                    {
                        for (int i = 0; i < positions.Count; i++)
                        {
                            if (uvVectors is not null && i < uvVectors.Count)
                            {
                                Vector2 uv = uvVectors[i];
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

                    Texture? texture = baseColorChannel?.Texture;
                    Image? image = SelectTextureImage(texture);
                    byte[]? textureBytes = image is { Content.IsEmpty: false } ? image.Content.Content.ToArray() : null;
                    string? textureExtension = ResolveImageExtension(image);

                    if (vertices.Count > 0 && indices.Count > 0)
                    {
                        string name = string.IsNullOrWhiteSpace(node.Name)
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

            foreach (Node? child in node.VisualChildren)
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

            return texture.PrimaryImage is { Content.IsEmpty: false } primary
                ? primary
                : texture.FallbackImage is { Content.IsEmpty: false } fallback ? fallback : texture.PrimaryImage ?? texture.FallbackImage;
        }

        private static string? ResolveImageExtension(Image? image)
        {
            if (image is null)
            {
                return ".png";
            }

            if (!string.IsNullOrWhiteSpace(image.Content.FileExtension))
            {
                return image.Content.FileExtension.StartsWith('.')
                    ? image.Content.FileExtension
                    : $".{image.Content.FileExtension}";
            }

            if (!string.IsNullOrWhiteSpace(image.Name))
            {
                string extFromName = Path.GetExtension(image.Name);
                if (!string.IsNullOrWhiteSpace(extFromName))
                {
                    return extFromName;
                }
            }

            if (!string.IsNullOrWhiteSpace(image.Content.MimeType))
            {
                if (string.Equals(image.Content.MimeType, "image/png", StringComparison.OrdinalIgnoreCase))
                {
                    return ".png";
                }

                if (string.Equals(image.Content.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    return ".jpg";
                }

                if (string.Equals(image.Content.MimeType, "image/webp", StringComparison.OrdinalIgnoreCase))
                {
                    return ".webp";
                }
            }

            return ".png";
        }
    }
}
