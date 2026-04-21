using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Google
{
    internal sealed class SearchResolver(GoogleGeocodingClient geocodingClient) : ISearchResolver
    {
        private readonly GoogleGeocodingClient _geocodingClient = geocodingClient;

        public Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken)
        {
            return _geocodingClient.SearchAsync(apiKey, query, cancellationToken);
        }
    }
}
