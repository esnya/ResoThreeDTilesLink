namespace ThreeDTilesLink.Core.Math
{
    public readonly struct Vector3d(double x, double y, double z) : IEquatable<Vector3d>
    {
        public double X { get; } = x;
        public double Y { get; } = y;
        public double Z { get; } = z;

        public static Vector3d operator +(Vector3d left, Vector3d right)
        {
            return new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        }

        public static Vector3d operator -(Vector3d left, Vector3d right)
        {
            return new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        }

        public static Vector3d operator *(double scalar, Vector3d value)
        {
            return new(scalar * value.X, scalar * value.Y, scalar * value.Z);
        }

        public static Vector3d operator *(Vector3d value, double scalar)
        {
            return scalar * value;
        }

        public static Vector3d operator /(Vector3d value, double scalar)
        {
            return new(value.X / scalar, value.Y / scalar, value.Z / scalar);
        }

        public double Length()
        {
            return System.Math.Sqrt((X * X) + (Y * Y) + (Z * Z));
        }

        public static double Dot(Vector3d a, Vector3d b)
        {
            return (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        }

        public static Vector3d Cross(Vector3d a, Vector3d b)
        {
            return new(
                (a.Y * b.Z) - (a.Z * b.Y),
                (a.Z * b.X) - (a.X * b.Z),
                (a.X * b.Y) - (a.Y * b.X));
        }

        public static Vector3d Normalize(Vector3d v)
        {
            double len = v.Length();
            return len <= 1e-12d ? new Vector3d(0d, 0d, 0d) : v / len;
        }

        public static Vector3d Add(Vector3d left, Vector3d right)
        {
            return left + right;
        }

        public bool Equals(Vector3d other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is Vector3d other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }

        public static bool operator ==(Vector3d left, Vector3d right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Vector3d left, Vector3d right)
        {
            return !(left == right);
        }
    }
}
