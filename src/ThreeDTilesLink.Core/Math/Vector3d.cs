namespace ThreeDTilesLink.Core.Math;

public readonly struct Vector3d
{
    public Vector3d(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static Vector3d operator +(Vector3d left, Vector3d right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Vector3d operator -(Vector3d left, Vector3d right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static Vector3d operator *(double scalar, Vector3d value) =>
        new(scalar * value.X, scalar * value.Y, scalar * value.Z);

    public static Vector3d operator *(Vector3d value, double scalar) => scalar * value;
    public static Vector3d operator /(Vector3d value, double scalar) =>
        new(value.X / scalar, value.Y / scalar, value.Z / scalar);

    public double Length() => System.Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    public static double Dot(Vector3d a, Vector3d b) =>
        (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vector3d Cross(Vector3d a, Vector3d b) =>
        new(
            (a.Y * b.Z) - (a.Z * b.Y),
            (a.Z * b.X) - (a.X * b.Z),
            (a.X * b.Y) - (a.Y * b.X));

    public static Vector3d Normalize(Vector3d v)
    {
        var len = v.Length();
        if (len <= 1e-12d)
        {
            return new Vector3d(0d, 0d, 0d);
        }

        return v / len;
    }
}
