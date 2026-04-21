namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IB3dmGlbExtractor
    {
        byte[] ExtractGlb(byte[] b3dmBytes);
    }
}
