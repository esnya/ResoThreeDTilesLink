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
            using var handler = new RecordingHttpMessageHandler(
                _ => CreateJsonResponse(
                    "https://tile.googleapis.com/v1/3dtiles/root.json?session=session-a&key=test-key",
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
            var parser = new TilesetParser();
            var source = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json?session=session-a"),
                new TileSourceAccess("test-key", null),
                TileSourceContentLinkOptions.CreateGoogleDefaults());
            var sut = new HttpTilesSource(httpClient, parser, new TileContentDecoder(parser, new B3dmGlbExtractor()));

            Tileset first = await sut.FetchRootTilesetAsync(source, CancellationToken.None);
            Tileset second = await sut.FetchRootTilesetAsync(source, CancellationToken.None);

            _ = handler.Requests.Should().HaveCount(1);
            _ = handler.Requests[0].RequestUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/root.json?session=session-a&key=test-key");
            _ = first.Should().NotBeSameAs(second);
            _ = first.Root.Children[0].ContentUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/nested.json?session=session-a");
            _ = second.Root.Children[0].ContentUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/nested.json?session=session-a");
        }

        [Fact]
        public async Task FetchNodeContentAsync_ReusesCachedNestedTilesetJson()
        {
            using var handler = new RecordingHttpMessageHandler(
                _ => CreateJsonResponse(
                    "https://tile.googleapis.com/v1/3dtiles/region/nested.json?session=session-b&key=test-key",
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
            var parser = new TilesetParser();
            var source = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json?session=session-b"),
                new TileSourceAccess("test-key", null),
                TileSourceContentLinkOptions.CreateGoogleDefaults());
            var sut = new HttpTilesSource(httpClient, parser, new TileContentDecoder(parser, new B3dmGlbExtractor()));
            var contentUri = new Uri("https://tile.googleapis.com/v1/3dtiles/region/nested.json?session=session-b");

            FetchedNodeContent first = await sut.FetchNodeContentAsync(contentUri, source, CancellationToken.None);
            FetchedNodeContent second = await sut.FetchNodeContentAsync(contentUri, source, CancellationToken.None);

            Tileset firstTileset = first.Should().BeOfType<NestedTilesetFetchedContent>().Subject.Tileset;
            Tileset secondTileset = second.Should().BeOfType<NestedTilesetFetchedContent>().Subject.Tileset;

            _ = handler.Requests.Should().HaveCount(1);
            _ = firstTileset.Should().NotBeSameAs(secondTileset);
            _ = firstTileset.Root.Children[0].ContentUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/region/leaf.glb?session=session-b");
            _ = secondTileset.Root.Children[0].ContentUri!.AbsoluteUri.Should().Be("https://tile.googleapis.com/v1/3dtiles/region/leaf.glb?session=session-b");
        }

        [Fact]
        public async Task FetchRootTilesetAsync_SendsBearerTokenHeader()
        {
            using var handler = new RecordingHttpMessageHandler(
                _ => CreateJsonResponse(
                    "https://plateau.example.com/tiles/root.json",
                    """
                    {
                      "root": {
                        "children": []
                      }
                    }
                    """));
            using var httpClient = new HttpClient(handler);
            var parser = new TilesetParser();
            var sut = new HttpTilesSource(httpClient, parser, new TileContentDecoder(parser, new B3dmGlbExtractor()));
            var source = new TileSourceOptions(
                new Uri("https://plateau.example.com/tiles/root.json"),
                new TileSourceAccess(null, "plateau-token"));

            _ = await sut.FetchRootTilesetAsync(source, CancellationToken.None);

            _ = handler.Requests.Should().ContainSingle();
            _ = handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
            _ = handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("plateau-token");
        }

        [Fact]
        public async Task FetchRootTilesetAsync_DoesNotShareCacheAcrossAuthContexts()
        {
            using var handler = new RecordingHttpMessageHandler(
                _ => CreateJsonResponse(
                    "https://tile.googleapis.com/v1/3dtiles/root.json?key=dynamic",
                    """
                    {
                      "root": {
                        "children": []
                      }
                    }
                    """));
            using var httpClient = new HttpClient(handler);
            var parser = new TilesetParser();
            var sut = new HttpTilesSource(httpClient, parser, new TileContentDecoder(parser, new B3dmGlbExtractor()));
            var firstSource = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json"),
                new TileSourceAccess("first-key", null));
            var secondSource = new TileSourceOptions(
                new Uri("https://tile.googleapis.com/v1/3dtiles/root.json"),
                new TileSourceAccess("second-key", null));

            _ = await sut.FetchRootTilesetAsync(firstSource, CancellationToken.None);
            _ = await sut.FetchRootTilesetAsync(secondSource, CancellationToken.None);

            _ = handler.Requests.Should().HaveCount(2);
            _ = handler.Requests.Select(static request => request.RequestUri!.Query)
                .Should()
                .Contain(["?key=first-key", "?key=second-key"]);
        }

        [Fact]
        public async Task FetchNodeContentAsync_DecodesB3dmPayloadToEmbeddedGlb()
        {
            byte[] glbBytes = [0x67, 0x6C, 0x54, 0x46, 0x02, 0x00, 0x00, 0x00];
            byte[] b3dmBytes = BuildB3dm(glbBytes);
            using var handler = new RecordingHttpMessageHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://plateau.example.com/data/leaf.b3dm"),
                    Content = new ByteArrayContent(b3dmBytes)
                });
            using var httpClient = new HttpClient(handler);
            var parser = new TilesetParser();
            var decoder = new TileContentDecoder(parser, new B3dmGlbExtractor());
            var sut = new HttpTilesSource(httpClient, parser, decoder);
            var source = new TileSourceOptions(
                new Uri("https://plateau.example.com/root.json"),
                new TileSourceAccess(null, null));

            FetchedNodeContent fetched = await sut.FetchNodeContentAsync(
                new Uri("https://plateau.example.com/data/leaf.b3dm"),
                source,
                CancellationToken.None);

            _ = fetched.Should().BeOfType<GlbFetchedContent>().Which.GlbBytes.Should().Equal(glbBytes);
        }

        private static byte[] BuildB3dm(byte[] glbBytes)
        {
            const uint featureTableJsonLength = 20;
            const uint featureTableBinaryLength = 0;
            const uint batchTableJsonLength = 0;
            const uint batchTableBinaryLength = 0;
            byte[] featureTableJson = Encoding.ASCII.GetBytes("{\"BATCH_LENGTH\":0}  ");
            uint byteLength = (uint)(28 + featureTableJson.Length + glbBytes.Length);
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("b3dm"));
            writer.Write(1u);
            writer.Write(byteLength);
            writer.Write(featureTableJsonLength);
            writer.Write(featureTableBinaryLength);
            writer.Write(batchTableJsonLength);
            writer.Write(batchTableBinaryLength);
            writer.Write(featureTableJson);
            writer.Write(glbBytes);
            writer.Flush();
            return buffer.ToArray();
        }

        private static HttpResponseMessage CreateJsonResponse(string requestUri, string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri),
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

            public List<HttpRequestMessage> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(Clone(request));
                return Task.FromResult(_responder(request));
            }

            private static HttpRequestMessage Clone(HttpRequestMessage request)
            {
                var clone = new HttpRequestMessage(request.Method, request.RequestUri);
                foreach (var header in request.Headers)
                {
                    _ = clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                return clone;
            }
        }
    }
}
