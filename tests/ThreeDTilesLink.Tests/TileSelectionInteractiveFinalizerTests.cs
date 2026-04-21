using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Generic;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.Tests
{
    public sealed class TileSelectionInteractiveFinalizerTests
    {
        [Fact]
        public async Task ApplyFinalLicenseCreditAsync_ClearsCreditString_WhenGenericRetainedTilesAreEmpty()
        {
            var metadataPort = new FakeMetadataPort();
            var sut = new TileSelectionInteractiveFinalizer(
                new NoOpResoniteSession(),
                metadataPort,
                new GenericTileLicenseCreditPolicy(),
                NullLogger<TileSelectionInteractiveFinalizer>.Instance);
            var request = new TileRunRequest(
                new GeoReference(0d, 0d, 0d),
                new GeoReference(0d, 0d, 0d),
                new TraversalOptions(500d, 40d, 4d),
                new ResoniteOutputOptions("127.0.0.1", 12345, false, true),
                new TileSourceOptions(
                    new Uri("https://plateau.example.com/root.json"),
                    new TileSourceAccess(null, null)));

            await sut.ApplyFinalLicenseCreditAsync(
                request,
                new Dictionary<string, RetainedTileState>(StringComparer.Ordinal),
                CancellationToken.None);

            _ = metadataPort.LicenseCredits.Should().ContainSingle().Which.Should().Be(string.Empty);
        }

        private sealed class NoOpResoniteSession : IResoniteSession
        {
            public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
                => Task.FromResult<string?>(null);

            public Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FakeMetadataPort : IResoniteSessionMetadataPort
        {
            public List<string> LicenseCredits { get; } = [];

            public Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
            {
                _ = cancellationToken;
                LicenseCredits.Add(creditString);
                return Task.CompletedTask;
            }

            public Task SetProgressAsync(string? parentSlotId, float progress01, string progressText, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task SetProgressValueAsync(string? parentSlotId, float progress01, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task SetProgressTextAsync(string? parentSlotId, string progressText, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
    }
}
