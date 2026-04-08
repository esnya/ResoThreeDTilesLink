namespace ThreeDTilesLink.Core.Models
{
    internal sealed record QueryRange
    {
        internal QueryRange(double rangeM)
        {
            if (!double.IsFinite(rangeM) || rangeM <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(rangeM), rangeM, "Range must be a finite positive number.");
            }

            RangeM = rangeM;
        }

        internal double RangeM { get; }

        internal double Min => -RangeM;

        internal double Max => RangeM;
    }
}
