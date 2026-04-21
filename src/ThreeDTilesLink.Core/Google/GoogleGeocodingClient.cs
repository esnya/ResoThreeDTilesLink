using System.Text.Json;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Google
{
    internal sealed class GoogleGeocodingClient(HttpClient httpClient)
    {
        private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        internal async Task<LocationSearchResult?> SearchAsync(string apiKey, string query, CancellationToken cancellationToken)
        {
            string normalizedApiKey = apiKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedApiKey))
            {
                throw new ArgumentException("A search API key is required for Google geocoding search.", nameof(apiKey));
            }

            string normalizedQuery = query?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                throw new ArgumentException("Search query cannot be empty.", nameof(query));
            }

            using HttpResponseMessage response = await _httpClient.GetAsync(
                BuildRequestUri(normalizedApiKey, normalizedQuery),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    FormatHttpFailure(response.StatusCode, response.ReasonPhrase, responseBody),
                    null,
                    response.StatusCode);
            }

            using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            {
                using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("status", out JsonElement statusElement))
                {
                    throw new InvalidOperationException("Google geocoding response is missing the status field.");
                }

                string status = statusElement.GetString() ?? string.Empty;
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

                if (!root.TryGetProperty("results", out JsonElement resultsElement) ||
                    resultsElement.ValueKind != JsonValueKind.Array ||
                    resultsElement.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException("Google geocoding returned status 'OK' without any results.");
                }

                JsonElement firstResult = resultsElement[0];
                if (firstResult.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Google geocoding returned an invalid first result.");
                }

                string formattedAddress = firstResult.TryGetProperty("formatted_address", out JsonElement formattedAddressElement)
                    ? formattedAddressElement.GetString() ?? normalizedQuery
                    : normalizedQuery;

                if (!firstResult.TryGetProperty("geometry", out JsonElement geometryElement) ||
                    geometryElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Google geocoding result is missing geometry data.");
                }

                if (!geometryElement.TryGetProperty("location", out JsonElement locationElement) ||
                    locationElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Google geocoding result is missing location data.");
                }

                if (!TryGetCoordinate(locationElement, "lat", out double latitude) ||
                    !TryGetCoordinate(locationElement, "lng", out double longitude))
                {
                    throw new InvalidOperationException("Google geocoding result is missing valid geometry location coordinates.");
                }

                return new LocationSearchResult(
                    formattedAddress,
                    latitude,
                    longitude);
            }
        }

        private static Uri BuildRequestUri(string apiKey, string query)
        {
            return new Uri(
                $"https://maps.googleapis.com/maps/api/geocode/json?address={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}",
                UriKind.Absolute);
        }

        private static bool TryGetCoordinate(JsonElement locationElement, string name, out double value)
        {
            if (locationElement.TryGetProperty(name, out JsonElement coordinateElement) &&
                coordinateElement.ValueKind == JsonValueKind.Number &&
                coordinateElement.TryGetDouble(out value) &&
                double.IsFinite(value))
            {
                return true;
            }

            value = default;
            return false;
        }

        private static string FormatHttpFailure(
            System.Net.HttpStatusCode statusCode,
            string? reasonPhrase,
            string responseBody)
        {
            const int MaxBodyLength = 256;
            string bodyPreview = responseBody.Length <= MaxBodyLength
                ? responseBody
                : $"{responseBody[..MaxBodyLength]}...";

            return string.IsNullOrWhiteSpace(bodyPreview)
                ? $"Google geocoding HTTP {(int)statusCode} {reasonPhrase}."
                : $"Google geocoding HTTP {(int)statusCode} {reasonPhrase}. Body preview: {bodyPreview}";
        }
    }
}
