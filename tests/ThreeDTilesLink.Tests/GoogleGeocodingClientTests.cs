using System.Net;
using System.Text;
using FluentAssertions;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Models;

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

        [Fact]
        public async Task SearchAsync_HttpError_IncludesResponseBody()
        {
            using var handler = new StubHttpMessageHandler(
                """
                {
                  "error_message": "billing disabled"
                }
                """,
                HttpStatusCode.Forbidden);
            using var httpClient = new HttpClient(handler);
            var sut = new GoogleGeocodingClient(httpClient);

            Func<Task> act = () => sut.SearchAsync("test-key", "Tokyo Tower", CancellationToken.None);

            _ = await act.Should().ThrowAsync<HttpRequestException>()
                .WithMessage("*billing disabled*");
        }

        [Fact]
        public async Task SearchAsync_OkWithoutResults_ThrowsMeaningfulError()
        {
            using var handler = new StubHttpMessageHandler(
                """
                {
                  "status": "OK",
                  "results": []
                }
                """);
            using var httpClient = new HttpClient(handler);
            var sut = new GoogleGeocodingClient(httpClient);

            Func<Task> act = () => sut.SearchAsync("test-key", "Tokyo Tower", CancellationToken.None);

            _ = await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*without any results*");
        }

        [Fact]
        public async Task SearchAsync_OkWithoutGeometry_ThrowsMeaningfulError()
        {
            using var handler = new StubHttpMessageHandler(
                """
                {
                  "status": "OK",
                  "results": [
                    {
                      "formatted_address": "Tokyo Tower, 4 Chome-2-8 Shibakoen, Minato City, Tokyo 105-0011, Japan",
                      "foo": {}
                    }
                  ]
                }
                """);
            using var httpClient = new HttpClient(handler);
            var sut = new GoogleGeocodingClient(httpClient);

            Func<Task> act = () => sut.SearchAsync("test-key", "Tokyo Tower", CancellationToken.None);

            _ = await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*missing geometry data*");
        }

        [Fact]
        public async Task SearchAsync_OkWithoutLocationCoordinates_ThrowsMeaningfulError()
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
                          "lat": "35.6",
                          "lng": 139.7
                        }
                      }
                    }
                  ]
                }
                """);
            using var httpClient = new HttpClient(handler);
            var sut = new GoogleGeocodingClient(httpClient);

            Func<Task> act = () => sut.SearchAsync("test-key", "Tokyo Tower", CancellationToken.None);

            _ = await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*valid geometry location coordinates*");
        }

        [Fact]
        public async Task RunPerformanceSummary_OnlyCountsOwnMeterMeasurements()
        {
            using var first = new RunPerformanceSummary();
            using var second = new RunPerformanceSummary();

            first.AddFetch(TimeSpan.FromMilliseconds(2.5));
            await Task.Delay(5);

            _ = first.FetchMilliseconds.Should().BeGreaterThan(0);
            _ = second.FetchMilliseconds.Should().Be(0);
            long firstBaseline = first.FetchMilliseconds;

            second.AddFetch(TimeSpan.FromMilliseconds(3.5));
            await Task.Delay(5);

            _ = second.FetchMilliseconds.Should().BeGreaterThan(0);
            _ = first.FetchMilliseconds.Should().Be(firstBaseline);
        }

        private sealed class StubHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
        {
            private readonly string _responseBody = responseBody;
            private readonly HttpStatusCode _statusCode = statusCode;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
