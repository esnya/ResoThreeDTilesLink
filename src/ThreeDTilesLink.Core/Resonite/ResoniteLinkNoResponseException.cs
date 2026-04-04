namespace ThreeDTilesLink.Core.Resonite
{
    public sealed class ResoniteLinkNoResponseException : InvalidOperationException
    {
        public ResoniteLinkNoResponseException()
            : base("ResoniteLink request returned no response.")
        {
        }

        public ResoniteLinkNoResponseException(string message)
            : base(message)
        {
        }

        public ResoniteLinkNoResponseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
