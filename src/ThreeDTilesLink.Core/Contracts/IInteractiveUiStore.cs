using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IInteractiveUiStore
    {
        Task<InteractiveUiBinding> CreateInteractiveUiBindingAsync(CancellationToken cancellationToken);
        Task<SelectionInputValues?> ReadInteractiveUiValuesAsync(InteractiveUiBinding binding, CancellationToken cancellationToken);
        Task<string?> ReadInteractiveUiSearchAsync(InteractiveUiBinding binding, CancellationToken cancellationToken);
        Task UpdateInteractiveUiCoordinatesAsync(InteractiveUiBinding binding, double latitude, double longitude, CancellationToken cancellationToken);
    }
}
