using ThreeDTilesLink.Core.Google;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ISearchResolver
    {
        Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken);
    }
}
