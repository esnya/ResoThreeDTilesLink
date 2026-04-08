using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Resonite
{
    internal sealed class ResoniteSelectedTileProjector(IResoniteSession session) : ISelectedTileProjector
    {
        private readonly IResoniteSession _session = session;

        public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            return _session.ConnectAsync(host, port, cancellationToken);
        }

        public Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
        {
            return _session.SetSessionLicenseCreditAsync(creditString, cancellationToken);
        }

        public Task SetProgressAsync(string? parentSlotId, float progress01, string progressText, CancellationToken cancellationToken)
        {
            return _session.SetProgressAsync(parentSlotId, progress01, progressText, cancellationToken);
        }

        public Task SetProgressValueAsync(string? parentSlotId, float progress01, CancellationToken cancellationToken)
        {
            return _session.SetProgressValueAsync(parentSlotId, progress01, cancellationToken);
        }

        public Task SetProgressTextAsync(string? parentSlotId, string progressText, CancellationToken cancellationToken)
        {
            return _session.SetProgressTextAsync(parentSlotId, progressText, cancellationToken);
        }

        public Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
        {
            return _session.StreamPlacedMeshAsync(payload, cancellationToken);
        }

        public Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
        {
            return _session.RemoveSlotAsync(slotId, cancellationToken);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return _session.DisconnectAsync(cancellationToken);
        }
    }
}
