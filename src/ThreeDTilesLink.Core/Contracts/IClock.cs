namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IClock
    {
        DateTimeOffset UtcNow { get; }

        Task Delay(TimeSpan delay, CancellationToken cancellationToken);
    }
}
