namespace ThreeDTilesLink.Core.Math
{
    /// <summary>
    /// 64-bit vector used for 3D Tiles coordinate calculations.
    /// </summary>
    internal readonly struct Vector3d(double x, double y, double z) : IEquatable<Vector3d>
    {
        /// <summary>
        /// X coordinate.
        /// </summary>
        public double X { get; } = x;
        /// <summary>
        /// Y coordinate.
        /// </summary>
        public double Y { get; } = y;
        /// <summary>
        /// Z coordinate.
        /// </summary>
        public double Z { get; } = z;

        /// <summary>
        /// Adds two vectors.
        /// </summary>
        public static Vector3d operator +(Vector3d left, Vector3d right)
        {
            return new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        }

        /// <summary>
        /// Subtracts the second vector from the first.
        /// </summary>
        public static Vector3d operator -(Vector3d left, Vector3d right)
        {
            return new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        }

        /// <summary>
        /// Multiplies a scalar by a vector.
        /// </summary>
        public static Vector3d operator *(double scalar, Vector3d value)
        {
            return new(scalar * value.X, scalar * value.Y, scalar * value.Z);
        }

        /// <summary>
        /// Multiplies a vector by a scalar.
        /// </summary>
        public static Vector3d operator *(Vector3d value, double scalar)
        {
            return scalar * value;
        }

        /// <summary>
        /// Divides each component by a scalar.
        /// </summary>
        public static Vector3d operator /(Vector3d value, double scalar)
        {
            return new(value.X / scalar, value.Y / scalar, value.Z / scalar);
        }

        /// <summary>
        /// Computes vector length.
        /// </summary>
        public double Length()
        {
            return System.Math.Sqrt((X * X) + (Y * Y) + (Z * Z));
        }

        /// <summary>
        /// Calculates the dot product.
        /// </summary>
        public static double Dot(Vector3d a, Vector3d b)
        {
            return (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        }

        /// <summary>
        /// Calculates the cross product.
        /// </summary>
        public static Vector3d Cross(Vector3d a, Vector3d b)
        {
            return new(
                (a.Y * b.Z) - (a.Z * b.Y),
                (a.Z * b.X) - (a.X * b.Z),
                (a.X * b.Y) - (a.Y * b.X));
        }

        /// <summary>
        /// Returns a normalized vector, or zero if the vector is too small.
        /// </summary>
        public static Vector3d Normalize(Vector3d v)
        {
            double len = v.Length();
            return len <= 1e-12d ? new Vector3d(0d, 0d, 0d) : v / len;
        }

        /// <summary>
        /// Adds two vectors.
        /// </summary>
        public static Vector3d Add(Vector3d left, Vector3d right)
        {
            return left + right;
        }

        /// <summary>
        /// Subtracts two vectors.
        /// </summary>
        public static Vector3d Subtract(Vector3d left, Vector3d right)
        {
            return left - right;
        }

        /// <summary>
        /// Multiplies a vector by a scalar.
        /// </summary>
        public static Vector3d Multiply(Vector3d value, double scalar)
        {
            return scalar * value;
        }

        /// <summary>
        /// Multiplies a scalar by a vector.
        /// </summary>
        public static Vector3d Multiply(double scalar, Vector3d value)
        {
            return scalar * value;
        }

        /// <summary>
        /// Divides each component by a scalar.
        /// </summary>
        public static Vector3d Divide(Vector3d value, double scalar)
        {
            return value / scalar;
        }

        /// <summary>
        /// Compares vector components for equality.
        /// </summary>
        public bool Equals(Vector3d other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        /// <summary>
        /// Compares another object to this vector.
        /// </summary>
        public override bool Equals(object? obj)
        {
            return obj is Vector3d other && Equals(other);
        }

        /// <summary>
        /// Gets a hash code for this vector.
        /// </summary>
        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }

        /// <summary>
        /// Equality operator.
        /// </summary>
        public static bool operator ==(Vector3d left, Vector3d right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Inequality operator.
        /// </summary>
        public static bool operator !=(Vector3d left, Vector3d right)
        {
            return !(left == right);
        }
    }
}
