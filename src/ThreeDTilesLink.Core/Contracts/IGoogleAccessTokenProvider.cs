namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IGoogleAccessTokenProvider
    {
        Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
    }
}
