using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IWatchStore
    {
        Task<WatchBinding> CreateWatchAsync(CancellationToken cancellationToken);
        Task<SelectionInputValues?> ReadSelectionInputValuesAsync(WatchBinding binding, CancellationToken cancellationToken);
        Task<string?> ReadWatchSearchAsync(WatchBinding binding, CancellationToken cancellationToken);
        Task UpdateWatchCoordinatesAsync(WatchBinding binding, double latitude, double longitude, CancellationToken cancellationToken);
    }
}
