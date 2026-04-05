namespace ThreeDTilesLink.Core.Models
{
    internal sealed record QueryRange(double RangeM)
    {
        internal double Min => -RangeM;
        internal double Max => RangeM;
    }
}
