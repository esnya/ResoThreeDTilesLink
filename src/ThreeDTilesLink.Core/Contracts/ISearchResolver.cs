using ThreeDTilesLink.Core.Google;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface ISearchResolver
    {
        Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken);
    }
}
