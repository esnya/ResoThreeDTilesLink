namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class InteractiveUiDisconnectedException : Exception
    {
        public InteractiveUiDisconnectedException()
        {
        }

        public InteractiveUiDisconnectedException(string message)
            : base(message)
        {
        }

        public InteractiveUiDisconnectedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class InteractiveUiNoResponseException : TimeoutException
    {
        public InteractiveUiNoResponseException()
        {
        }

        public InteractiveUiNoResponseException(string message)
            : base(message)
        {
        }

        public InteractiveUiNoResponseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
