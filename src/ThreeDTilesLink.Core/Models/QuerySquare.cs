namespace ThreeDTilesLink.Core.Models
{
    public sealed record QuerySquare(double HalfWidthM)
    {
        public double Min => -HalfWidthM;
        public double Max => HalfWidthM;
    }
}
