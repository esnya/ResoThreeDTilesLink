using System.Net;
using System.Text;
using FluentAssertions;
using ThreeDTilesLink.Core.Google;

namespace ThreeDTilesLink.Tests
{
    public sealed class GoogleGeocodingClientTests
    {
        [Fact]
        public async Task SearchAsync_OkResponse_ReturnsFirstLocation()
        {
            using var handler = new StubHttpMessageHandler(
                """
                {
                  "status": "OK",
                  "results": [
                    {
                      "formatted_address": "Tokyo Tower, 4 Chome-2-8 Shibakoen, Minato City, Tokyo 105-0011, Japan",
                      "geometry": {
                        "location": {
                          "lat": 35.6585805,
                          "lng": 139.7454329
                        }
                      }
                    }
                  ]
                }
                """);
            using var httpClient = new HttpClient(handler);
            var sut = new GoogleGeocodingClient(httpClient);

            LocationSearchResult? result = await sut.SearchAsync("test-key", "Tokyo Tower", CancellationToken.None);

            _ = result.Should().NotBeNull();
            _ = result!.FormattedAddress.Should().Contain("Tokyo Tower");
            _ = result.Latitude.Should().Be(35.6585805d);
            _ = result.Longitude.Should().Be(139.7454329d);
        }

        [Fact]
        public async Task SearchAsync_ZeroResults_ReturnsNull()
        {
            using var handler = new StubHttpMessageHandler(
                """
                {
                  "status": "ZERO_RESULTS",
                  "results": []
                }
                """);
            using var httpClient = new HttpClient(handler);
            var sut = new GoogleGeocodingClient(httpClient);

            LocationSearchResult? result = await sut.SearchAsync("test-key", "nowhere", CancellationToken.None);

            _ = result.Should().BeNull();
        }

        [Fact]
        public async Task SearchAsync_ApiError_Throws()
        {
            using var handler = new StubHttpMessageHandler(
                """
                {
                  "status": "REQUEST_DENIED",
                  "error_message": "API keys with referer restrictions cannot be used with this API."
                }
                """);
            using var httpClient = new HttpClient(handler);
            var sut = new GoogleGeocodingClient(httpClient);

            Func<Task> act = () => sut.SearchAsync("test-key", "Tokyo Tower", CancellationToken.None);

            _ = await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*REQUEST_DENIED*");
        }

        private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
        {
            private readonly string _responseBody = responseBody;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
