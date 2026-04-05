using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Specialized;
using System.Web;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles
{
    internal sealed class HttpTilesSource(HttpClient httpClient) : ITilesSource
    {
        private static readonly Uri RootTilesetUri = new("https://tile.googleapis.com/v1/3dtiles/root.json");

        private readonly HttpClient _httpClient = httpClient;
        private readonly ConcurrentDictionary<string, CachedTilesetJson> _tilesetJsonCache = new(StringComparer.Ordinal);

        public async Task<Tileset> FetchRootTilesetAsync(GoogleTilesAuth auth, CancellationToken cancellationToken)
        {
            return await FetchTilesetCoreAsync(RootTilesetUri, auth, cancellationToken).ConfigureAwait(false);
        }

        public async Task<FetchedNodeContent> FetchNodeContentAsync(Uri contentUri, GoogleTilesAuth auth, CancellationToken cancellationToken)
        {
            return TileContentClassifier.Classify(contentUri) switch
            {
                TileContentKind.Json => new NestedTilesetFetchedContent(
                    await FetchTilesetCoreAsync(contentUri, auth, cancellationToken).ConfigureAwait(false)),
                TileContentKind.Glb => new GlbFetchedContent(
                    await FetchBinaryContentAsync(contentUri, auth, cancellationToken).ConfigureAwait(false)),
                _ => new UnsupportedFetchedContent($"Unsupported tile content URI: {contentUri}")
            };
        }

        private async Task<Tileset> FetchTilesetCoreAsync(Uri tilesetUri, GoogleTilesAuth auth, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auth);
            string cacheKey = BuildTilesetCacheKey(tilesetUri, auth);
            if (_tilesetJsonCache.TryGetValue(cacheKey, out CachedTilesetJson? cached))
            {
                return TilesetParser.Parse(cached.Json, cached.SourceUri);
            }

            using HttpRequestMessage request = CreateRequest(tilesetUri, auth);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Uri sourceUri = response.RequestMessage?.RequestUri ?? tilesetUri;
            _tilesetJsonCache[cacheKey] = new CachedTilesetJson(sourceUri, json);
            return TilesetParser.Parse(json, sourceUri);
        }

        private async Task<byte[]> FetchBinaryContentAsync(Uri contentUri, GoogleTilesAuth auth, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auth);
            using HttpRequestMessage request = CreateRequest(contentUri, auth);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        private static HttpRequestMessage CreateRequest(Uri uri, GoogleTilesAuth auth)
        {
            Uri target = uri;
            if (!string.IsNullOrWhiteSpace(auth.ApiKey))
            {
                target = AppendApiKey(uri, auth.ApiKey);
            }

            var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (!string.IsNullOrWhiteSpace(auth.BearerToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.BearerToken);
            }

            return request;
        }

        private static Uri AppendApiKey(Uri uri, string apiKey)
        {
            var builder = new UriBuilder(uri);
            NameValueCollection query = HttpUtility.ParseQueryString(builder.Query);
            if (string.IsNullOrWhiteSpace(query["key"]))
            {
                query["key"] = apiKey;
            }

            builder.Query = query.ToString() ?? string.Empty;
            return builder.Uri;
        }

        private static string BuildTilesetCacheKey(Uri tilesetUri, GoogleTilesAuth auth)
        {
            string authContext = !string.IsNullOrWhiteSpace(auth.ApiKey)
                ? $"key:{auth.ApiKey}"
                : !string.IsNullOrWhiteSpace(auth.BearerToken)
                    ? $"bearer:{auth.BearerToken}"
                    : "anonymous";
            string combined = $"{tilesetUri.AbsoluteUri}|{authContext}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined)));
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}",
                null,
                response.StatusCode);
        }

        private sealed record CachedTilesetJson(Uri SourceUri, string Json);
    }
}
