namespace ThreeDTilesLink.Core.Contracts
{
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }

        Task Delay(TimeSpan delay, CancellationToken cancellationToken);
    }
}
