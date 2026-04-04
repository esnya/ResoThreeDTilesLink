using System.Text.Json;

namespace ThreeDTilesLink.Core.Google
{
    public sealed class GoogleGeocodingClient(HttpClient httpClient)
    {
        private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        public async Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken)
        {
            string normalizedApiKey = apiKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedApiKey))
            {
                throw new InvalidOperationException("GOOGLE_MAPS_API_KEY is required for Google geocoding search.");
            }

            string normalizedQuery = query?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                throw new ArgumentException("Search query cannot be empty.", nameof(query));
            }

            using HttpResponseMessage response = await _httpClient.GetAsync(
                BuildRequestUri(normalizedApiKey, normalizedQuery),
                cancellationToken).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (responseStream.ConfigureAwait(false))
            {
                using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

                JsonElement root = document.RootElement;
                string status = root.GetProperty("status").GetString() ?? string.Empty;
                if (string.Equals(status, "ZERO_RESULTS", StringComparison.Ordinal))
                {
                    return null;
                }

                if (!string.Equals(status, "OK", StringComparison.Ordinal))
                {
                    string? errorMessage = root.TryGetProperty("error_message", out JsonElement errorElement)
                        ? errorElement.GetString()
                        : null;

                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(errorMessage)
                            ? $"Google geocoding failed with status '{status}'."
                            : $"Google geocoding failed with status '{status}': {errorMessage}");
                }

                JsonElement firstResult = root.GetProperty("results")[0];
                string formattedAddress = firstResult.TryGetProperty("formatted_address", out JsonElement formattedAddressElement)
                    ? formattedAddressElement.GetString() ?? normalizedQuery
                    : normalizedQuery;
                JsonElement location = firstResult.GetProperty("geometry").GetProperty("location");

                return new LocationSearchResult(
                    formattedAddress,
                    location.GetProperty("lat").GetDouble(),
                    location.GetProperty("lng").GetDouble());
            }
        }

        private static Uri BuildRequestUri(string apiKey, string query)
        {
            return new Uri(
                $"https://maps.googleapis.com/maps/api/geocode/json?address={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}",
                UriKind.Absolute);
        }
    }

    public sealed record LocationSearchResult(
        string FormattedAddress,
        double Latitude,
        double Longitude);
}
