using FluentAssertions;
using ThreeDTilesLink.Core.Math;

namespace ThreeDTilesLink.Tests
{
    public sealed class Matrix4x4dTests
    {
        [Fact]
        public void FromCesiumColumnMajor_TranslationMatrix_TransformsPoint()
        {
            var matrix = Matrix4x4d.FromCesiumColumnMajor(
            [
                1d, 0d, 0d, 0d,
                0d, 1d, 0d, 0d,
                0d, 0d, 1d, 0d,
                10d, 5d, -2d, 1d
            ]);

            Vector3d transformed = matrix.TransformPoint(new Vector3d(1d, 2d, 3d));

            _ = transformed.X.Should().Be(11d);
            _ = transformed.Y.Should().Be(7d);
            _ = transformed.Z.Should().Be(1d);
        }

        [Fact]
        public void MatrixComposition_ChildThenParent_IsAppliedInOrder()
        {
            var parent = Matrix4x4d.FromCesiumColumnMajor(
            [
                1d, 0d, 0d, 0d,
                0d, 1d, 0d, 0d,
                0d, 0d, 1d, 0d,
                10d, 0d, 0d, 1d
            ]);

            var child = Matrix4x4d.FromCesiumColumnMajor(
            [
                1d, 0d, 0d, 0d,
                0d, 1d, 0d, 0d,
                0d, 0d, 1d, 0d,
                0d, 5d, 0d, 1d
            ]);

            Matrix4x4d world = child * parent;
            Vector3d transformed = world.TransformPoint(new Vector3d(0d, 0d, 0d));

            _ = transformed.X.Should().Be(10d);
            _ = transformed.Y.Should().Be(5d);
            _ = transformed.Z.Should().Be(0d);
        }
    }
}
