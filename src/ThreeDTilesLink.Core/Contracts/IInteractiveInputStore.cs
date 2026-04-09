using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IInteractiveInputStore
    {
        Task<InteractiveInputBinding> CreateInteractiveInputBindingAsync(CancellationToken cancellationToken);
        Task<SelectionInputValues?> ReadInteractiveInputValuesAsync(InteractiveInputBinding binding, CancellationToken cancellationToken);
        Task<string?> ReadInteractiveInputSearchAsync(InteractiveInputBinding binding, CancellationToken cancellationToken);
        Task UpdateInteractiveInputCoordinatesAsync(InteractiveInputBinding binding, double latitude, double longitude, CancellationToken cancellationToken);
    }
}
