using System.Net;
using System.Text;
using FluentAssertions;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;

namespace ThreeDTilesLink.Tests
{
    public sealed class HttpTilesSourceTests
    {
        [Fact]
        public async Task FetchRootTilesetAsync_ReusesCachedJsonAcrossRepeatedRequests()
        {
            using var handler = new CountingHttpMessageHandler(
                _ => CreateJsonResponse(
                    "https://tile.googleapis.com/v1/3dtiles/root.json?session=session-a",
                    """
                    {
                      "root": {
                        "children": [
                          { "content": { "uri": "nested.json" } }
                        ]
                      }
                    }
                    """));
            using var httpClient = new HttpClient(handler);
            var sut = new HttpTilesSource(httpClient);
            var auth = new GoogleTilesAuth("test-key", null);

            Tileset first = await sut.FetchRootTilesetAsync(auth, CancellationToken.None);
            Tileset second = await sut.FetchRootTilesetAsync(auth, CancellationToken.None);

            _ = handler.RequestCount.Should().Be(1);
            _ = first.Should().NotBeSameAs(second);
            _ = first.Root.Children[0].ContentUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/nested.json?session=session-a");
            _ = second.Root.Children[0].ContentUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/nested.json?session=session-a");
        }

        [Fact]
        public async Task FetchNodeContentAsync_ReusesCachedNestedTilesetJson()
        {
            using var handler = new CountingHttpMessageHandler(
                _ => CreateJsonResponse(
                    "https://tile.googleapis.com/v1/3dtiles/region/nested.json?session=session-b",
                    """
                    {
                      "root": {
                        "children": [
                          { "content": { "uri": "leaf.glb" } }
                        ]
                      }
                    }
                    """));
            using var httpClient = new HttpClient(handler);
            var sut = new HttpTilesSource(httpClient);
            var auth = new GoogleTilesAuth("test-key", null);
            var contentUri = new Uri("https://tile.googleapis.com/v1/3dtiles/region/nested.json?session=session-b");

            FetchedNodeContent first = await sut.FetchNodeContentAsync(contentUri, auth, CancellationToken.None);
            FetchedNodeContent second = await sut.FetchNodeContentAsync(contentUri, auth, CancellationToken.None);

            Tileset firstTileset = first.Should().BeOfType<NestedTilesetFetchedContent>().Subject.Tileset;
            Tileset secondTileset = second.Should().BeOfType<NestedTilesetFetchedContent>().Subject.Tileset;

            _ = handler.RequestCount.Should().Be(1);
            _ = firstTileset.Should().NotBeSameAs(secondTileset);
            _ = firstTileset.Root.Children[0].ContentUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/region/leaf.glb?session=session-b");
            _ = secondTileset.Root.Children[0].ContentUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/region/leaf.glb?session=session-b");
        }

        private static HttpResponseMessage CreateJsonResponse(string requestUri, string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri),
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        private sealed class CountingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return Task.FromResult(_responder(request));
            }
        }
    }
}
