namespace ThreeDTilesLink.Core.Math
{
    public readonly struct Matrix4x4d(
        double m11, double m12, double m13, double m14,
        double m21, double m22, double m23, double m24,
        double m31, double m32, double m33, double m34,
        double m41, double m42, double m43, double m44) : IEquatable<Matrix4x4d>
    {
        public double M11 { get; } = m11; public double M12 { get; } = m12; public double M13 { get; } = m13; public double M14 { get; } = m14;
        public double M21 { get; } = m21; public double M22 { get; } = m22; public double M23 { get; } = m23; public double M24 { get; } = m24;
        public double M31 { get; } = m31; public double M32 { get; } = m32; public double M33 { get; } = m33; public double M34 { get; } = m34;
        public double M41 { get; } = m41; public double M42 { get; } = m42; public double M43 { get; } = m43; public double M44 { get; } = m44;

        public static Matrix4x4d Identity => new(
            1d, 0d, 0d, 0d,
            0d, 1d, 0d, 0d,
            0d, 0d, 1d, 0d,
            0d, 0d, 0d, 1d);

        public static Matrix4x4d operator *(Matrix4x4d left, Matrix4x4d right)
        {
            return Multiply(left, right);
        }

        public static Matrix4x4d FromCesiumColumnMajor(IReadOnlyList<double> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            return values.Count != 16
                ? throw new ArgumentException("Transform matrix must have 16 elements.", nameof(values))
                : new Matrix4x4d(
                values[0], values[1], values[2], values[3],
                values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11],
                values[12], values[13], values[14], values[15]);
        }

        public static Matrix4x4d FromNumerics(System.Numerics.Matrix4x4 value)
        {
            return new Matrix4x4d(
                value.M11, value.M12, value.M13, value.M14,
                value.M21, value.M22, value.M23, value.M24,
                value.M31, value.M32, value.M33, value.M34,
                value.M41, value.M42, value.M43, value.M44);
        }

        public static Matrix4x4d Multiply(Matrix4x4d left, Matrix4x4d right)
        {
            return new Matrix4x4d(
                (left.M11 * right.M11) + (left.M12 * right.M21) + (left.M13 * right.M31) + (left.M14 * right.M41),
                (left.M11 * right.M12) + (left.M12 * right.M22) + (left.M13 * right.M32) + (left.M14 * right.M42),
                (left.M11 * right.M13) + (left.M12 * right.M23) + (left.M13 * right.M33) + (left.M14 * right.M43),
                (left.M11 * right.M14) + (left.M12 * right.M24) + (left.M13 * right.M34) + (left.M14 * right.M44),

                (left.M21 * right.M11) + (left.M22 * right.M21) + (left.M23 * right.M31) + (left.M24 * right.M41),
                (left.M21 * right.M12) + (left.M22 * right.M22) + (left.M23 * right.M32) + (left.M24 * right.M42),
                (left.M21 * right.M13) + (left.M22 * right.M23) + (left.M23 * right.M33) + (left.M24 * right.M43),
                (left.M21 * right.M14) + (left.M22 * right.M24) + (left.M23 * right.M34) + (left.M24 * right.M44),

                (left.M31 * right.M11) + (left.M32 * right.M21) + (left.M33 * right.M31) + (left.M34 * right.M41),
                (left.M31 * right.M12) + (left.M32 * right.M22) + (left.M33 * right.M32) + (left.M34 * right.M42),
                (left.M31 * right.M13) + (left.M32 * right.M23) + (left.M33 * right.M33) + (left.M34 * right.M43),
                (left.M31 * right.M14) + (left.M32 * right.M24) + (left.M33 * right.M34) + (left.M34 * right.M44),

                (left.M41 * right.M11) + (left.M42 * right.M21) + (left.M43 * right.M31) + (left.M44 * right.M41),
                (left.M41 * right.M12) + (left.M42 * right.M22) + (left.M43 * right.M32) + (left.M44 * right.M42),
                (left.M41 * right.M13) + (left.M42 * right.M23) + (left.M43 * right.M33) + (left.M44 * right.M43),
                (left.M41 * right.M14) + (left.M42 * right.M24) + (left.M43 * right.M34) + (left.M44 * right.M44));
        }

        public Vector3d TransformPoint(Vector3d point)
        {
            double x = (point.X * M11) + (point.Y * M21) + (point.Z * M31) + M41;
            double y = (point.X * M12) + (point.Y * M22) + (point.Z * M32) + M42;
            double z = (point.X * M13) + (point.Y * M23) + (point.Z * M33) + M43;
            double w = (point.X * M14) + (point.Y * M24) + (point.Z * M34) + M44;

            if (System.Math.Abs(w) > 1e-12)
            {
                double invW = 1.0 / w;
                return new Vector3d(x * invW, y * invW, z * invW);
            }

            return new Vector3d(x, y, z);
        }

        public Vector3d TransformDirection(Vector3d direction)
        {
            double x = (direction.X * M11) + (direction.Y * M21) + (direction.Z * M31);
            double y = (direction.X * M12) + (direction.Y * M22) + (direction.Z * M32);
            double z = (direction.X * M13) + (direction.Y * M23) + (direction.Z * M33);
            return new Vector3d(x, y, z);
        }

        public double MaxLinearScale()
        {
            double x = TransformDirection(new Vector3d(1d, 0d, 0d)).Length();
            double y = TransformDirection(new Vector3d(0d, 1d, 0d)).Length();
            double z = TransformDirection(new Vector3d(0d, 0d, 1d)).Length();
            return System.Math.Max(x, System.Math.Max(y, z));
        }

        public bool Equals(Matrix4x4d other)
        {
            return M11 == other.M11 && M12 == other.M12 && M13 == other.M13 && M14 == other.M14 &&
                   M21 == other.M21 && M22 == other.M22 && M23 == other.M23 && M24 == other.M24 &&
                   M31 == other.M31 && M32 == other.M32 && M33 == other.M33 && M34 == other.M34 &&
                   M41 == other.M41 && M42 == other.M42 && M43 == other.M43 && M44 == other.M44;
        }

        public override bool Equals(object? obj)
        {
            return obj is Matrix4x4d other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                HashCode.Combine(M11, M12, M13, M14),
                HashCode.Combine(M21, M22, M23, M24),
                HashCode.Combine(M31, M32, M33, M34),
                HashCode.Combine(M41, M42, M43, M44));
        }

        public static bool operator ==(Matrix4x4d left, Matrix4x4d right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Matrix4x4d left, Matrix4x4d right)
        {
            return !(left == right);
        }
    }
}
