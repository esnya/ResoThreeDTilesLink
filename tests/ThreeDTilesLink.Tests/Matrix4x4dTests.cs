using FluentAssertions;
using ThreeDTilesLink.Core.Math;

namespace ThreeDTilesLink.Tests;

public sealed class Matrix4x4dTests
{
    [Fact]
    public void FromCesiumColumnMajor_TranslationMatrix_TransformsPoint()
    {
        var matrix = Matrix4x4d.FromCesiumColumnMajor(new[]
        {
            1d, 0d, 0d, 0d,
            0d, 1d, 0d, 0d,
            0d, 0d, 1d, 0d,
            10d, 5d, -2d, 1d
        });

        var transformed = matrix.TransformPoint(new Vector3d(1d, 2d, 3d));

        transformed.X.Should().Be(11d);
        transformed.Y.Should().Be(7d);
        transformed.Z.Should().Be(1d);
    }

    [Fact]
    public void MatrixComposition_ChildThenParent_IsAppliedInOrder()
    {
        var parent = Matrix4x4d.FromCesiumColumnMajor(new[]
        {
            1d, 0d, 0d, 0d,
            0d, 1d, 0d, 0d,
            0d, 0d, 1d, 0d,
            10d, 0d, 0d, 1d
        });

        var child = Matrix4x4d.FromCesiumColumnMajor(new[]
        {
            1d, 0d, 0d, 0d,
            0d, 1d, 0d, 0d,
            0d, 0d, 1d, 0d,
            0d, 5d, 0d, 1d
        });

        var world = child * parent;
        var transformed = world.TransformPoint(new Vector3d(0d, 0d, 0d));

        transformed.X.Should().Be(10d);
        transformed.Y.Should().Be(5d);
        transformed.Z.Should().Be(0d);
    }
}
