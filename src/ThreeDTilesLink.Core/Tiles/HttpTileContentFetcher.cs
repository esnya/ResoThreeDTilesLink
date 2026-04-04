using System.Web;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Tiles;

public sealed class HttpTileContentFetcher : ITileContentFetcher
{
    private static readonly Uri RootTilesetUri = new("https://tile.googleapis.com/v1/3dtiles/root.json");

    private readonly HttpClient _httpClient;
    private readonly TilesetParser _parser;

    public HttpTileContentFetcher(HttpClient httpClient, TilesetParser parser)
    {
        _httpClient = httpClient;
        _parser = parser;
    }

    public async Task<Tileset> FetchRootTilesetAsync(GoogleTilesAuth auth, CancellationToken cancellationToken)
    {
        return await FetchTilesetAsync(RootTilesetUri, auth, cancellationToken);
    }

    public async Task<Tileset> FetchTilesetAsync(Uri tilesetUri, GoogleTilesAuth auth, CancellationToken cancellationToken)
    {
        var request = CreateRequest(tilesetUri, auth);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var sourceUri = response.RequestMessage?.RequestUri ?? tilesetUri;
        return _parser.Parse(json, sourceUri);
    }

    public async Task<byte[]> FetchTileContentAsync(Uri contentUri, GoogleTilesAuth auth, CancellationToken cancellationToken)
    {
        var request = CreateRequest(contentUri, auth);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(Uri uri, GoogleTilesAuth auth)
    {
        var target = uri;
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
        var query = HttpUtility.ParseQueryString(builder.Query);
        if (string.IsNullOrWhiteSpace(query["key"]))
        {
            query["key"] = apiKey;
        }

        builder.Query = query.ToString() ?? string.Empty;
        return builder.Uri;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}",
            null,
            response.StatusCode);
    }
}
