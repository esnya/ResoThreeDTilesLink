using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    public interface IProbeStore
    {
        Task<ProbeBinding> CreateProbeAsync(ProbeConfiguration configuration, CancellationToken cancellationToken);
        Task<ProbeValues?> ReadProbeValuesAsync(ProbeBinding binding, CancellationToken cancellationToken);
        Task<string?> ReadProbeSearchAsync(ProbeBinding binding, CancellationToken cancellationToken);
        Task UpdateProbeCoordinatesAsync(ProbeBinding binding, double latitude, double longitude, CancellationToken cancellationToken);
    }
}
