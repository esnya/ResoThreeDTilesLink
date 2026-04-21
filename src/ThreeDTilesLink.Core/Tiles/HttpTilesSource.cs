using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Specialized;
using System.Web;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles
{
    internal sealed class HttpTilesSource(
        HttpClient httpClient,
        ITilesetParser tilesetParser,
        RunPerformanceSummary? performanceSummary = null) : ITilesSource
    {
        private readonly HttpClient _httpClient = httpClient;
        private readonly ITilesetParser _tilesetParser = tilesetParser;
        private readonly RunPerformanceSummary? _performanceSummary = performanceSummary;
        private readonly ConcurrentDictionary<string, CachedTilesetJson> _tilesetJsonCache = new(StringComparer.Ordinal);

        public async Task<Tileset> FetchRootTilesetAsync(TileSourceOptions source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            return await FetchTilesetCoreAsync(source.RootTilesetUri, source, cancellationToken).ConfigureAwait(false);
        }

        public async Task<FetchedNodeContent> FetchNodeContentAsync(Uri contentUri, TileSourceOptions source, CancellationToken cancellationToken)
        {
            return TileContentClassifier.Classify(contentUri) switch
            {
                TileContentKind.Json => new NestedTilesetFetchedContent(
                    await FetchTilesetCoreAsync(contentUri, source, cancellationToken).ConfigureAwait(false)),
                TileContentKind.Glb => new GlbFetchedContent(
                    await FetchBinaryContentAsync(contentUri, source, cancellationToken).ConfigureAwait(false)),
                _ => new UnsupportedFetchedContent($"Unsupported tile content URI: {contentUri}")
            };
        }

        private async Task<Tileset> FetchTilesetCoreAsync(Uri tilesetUri, TileSourceOptions source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            string cacheKey = BuildTilesetCacheKey(tilesetUri, source.Access);
            if (_tilesetJsonCache.TryGetValue(cacheKey, out CachedTilesetJson? cached))
            {
                return _tilesetParser.Parse(cached.Json, source, cached.SourceUri);
            }

            using HttpRequestMessage request = CreateRequest(tilesetUri, source.Access);
            RunPerformanceSummary? performanceSummary = _performanceSummary;
            DateTimeOffset startedAt = performanceSummary is null ? default : DateTimeOffset.UtcNow;
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(contentStream);
            string json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            Uri sourceUri = response.RequestMessage?.RequestUri ?? tilesetUri;
            _tilesetJsonCache[cacheKey] = new CachedTilesetJson(sourceUri, json);
            if (performanceSummary is not null)
            {
                performanceSummary.AddFetch(DateTimeOffset.UtcNow - startedAt);
            }

            return _tilesetParser.Parse(json, source, sourceUri);
        }

        private async Task<byte[]> FetchBinaryContentAsync(Uri contentUri, TileSourceOptions source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            using HttpRequestMessage request = CreateRequest(contentUri, source.Access);
            RunPerformanceSummary? performanceSummary = _performanceSummary;
            DateTimeOffset startedAt = performanceSummary is null ? default : DateTimeOffset.UtcNow;
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await contentStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();
            if (performanceSummary is not null)
            {
                performanceSummary.AddFetch(DateTimeOffset.UtcNow - startedAt);
            }

            return bytes;
        }

        private static HttpRequestMessage CreateRequest(Uri uri, TileSourceAccess access)
        {
            Uri target = uri;
            if (!string.IsNullOrWhiteSpace(access.ApiKey))
            {
                target = AppendApiKey(uri, access.ApiKey);
            }

            var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (!string.IsNullOrWhiteSpace(access.BearerToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access.BearerToken);
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

        private static string BuildTilesetCacheKey(Uri tilesetUri, TileSourceAccess access)
        {
            string authContext = !string.IsNullOrWhiteSpace(access.ApiKey)
                ? $"key:{access.ApiKey}"
                : !string.IsNullOrWhiteSpace(access.BearerToken)
                    ? $"bearer:{access.BearerToken}"
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

            using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(contentStream);
            string body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                FormatHttpFailure(response.StatusCode, response.ReasonPhrase, body),
                null,
                response.StatusCode);
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
                ? $"HTTP {(int)statusCode} {reasonPhrase}."
                : $"HTTP {(int)statusCode} {reasonPhrase}. Body preview: {bodyPreview}";
        }

        private sealed record CachedTilesetJson(Uri SourceUri, string Json);
    }
}
