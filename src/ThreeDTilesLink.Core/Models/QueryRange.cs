namespace ThreeDTilesLink.Core.Models
{
    public sealed record QueryRange(double RangeM)
    {
        public double Min => -RangeM;
        public double Max => RangeM;
    }
}
