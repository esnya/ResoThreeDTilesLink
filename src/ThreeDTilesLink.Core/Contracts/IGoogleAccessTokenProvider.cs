namespace ThreeDTilesLink.Core.Contracts
{
    public interface IGoogleAccessTokenProvider
    {
        Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
    }
}
