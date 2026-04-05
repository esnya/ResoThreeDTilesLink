using ThreeDTilesLink.Core.Contracts;

namespace ThreeDTilesLink.Core.Runtime
{
    internal sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }
}
