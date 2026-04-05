namespace ThreeDTilesLink.Core.Resonite
{
    public sealed class ResoniteLinkDisconnectedException : InvalidOperationException
    {
        private const string DefaultMessage = "ResoniteLink is not connected.";

        public ResoniteLinkDisconnectedException()
            : base(DefaultMessage)
        {
        }

        public ResoniteLinkDisconnectedException(Exception? innerException)
            : base(DefaultMessage, innerException)
        {
        }

        public ResoniteLinkDisconnectedException(string message)
            : base(message)
        {
        }

        public ResoniteLinkDisconnectedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
