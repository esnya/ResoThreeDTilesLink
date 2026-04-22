using System.Net.WebSockets;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.Core.Resonite
{
    internal sealed class ResoniteDynamicValueInteractiveUiStore(ResoniteSession session) : IInteractiveUiStore
    {
        private readonly ResoniteSession _session = session;
        private readonly Dictionary<string, ResoniteDynamicValueInteractiveUiBinding> _bindings = new(StringComparer.Ordinal);

        public async Task<InteractiveUiBinding> CreateInteractiveUiBindingAsync(CancellationToken cancellationToken)
        {
            ResoniteDynamicValueInteractiveUiBinding binding = await _session
                .CreateResoniteDynamicValueInteractiveUiBindingAsync(cancellationToken)
                .ConfigureAwait(false);
            string token = Guid.NewGuid().ToString("N");
            _bindings[token] = binding;
            return new InteractiveUiBinding(token);
        }

        public Task<SelectionInputValues?> ReadInteractiveUiValuesAsync(InteractiveUiBinding binding, CancellationToken cancellationToken)
        {
            return NormalizeAsync(() => _session.ReadResoniteDynamicValueInteractiveUiValuesAsync(Resolve(binding), cancellationToken));
        }

        public Task<string?> ReadInteractiveUiSearchAsync(InteractiveUiBinding binding, CancellationToken cancellationToken)
        {
            return NormalizeAsync(() => _session.ReadResoniteDynamicValueInteractiveUiSearchAsync(Resolve(binding), cancellationToken));
        }

        public Task UpdateInteractiveUiCoordinatesAsync(InteractiveUiBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
        {
            return NormalizeAsync(() => _session.UpdateResoniteDynamicValueInteractiveUiCoordinatesAsync(Resolve(binding), latitude, longitude, cancellationToken));
        }

        internal ResoniteDynamicValueInteractiveUiBinding ResolveForTest(InteractiveUiBinding binding) => Resolve(binding);

        private ResoniteDynamicValueInteractiveUiBinding Resolve(InteractiveUiBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (!_bindings.TryGetValue(binding.Token, out ResoniteDynamicValueInteractiveUiBinding? resolved))
            {
                throw new InvalidOperationException("Interactive UI binding token is not registered.");
            }

            return resolved;
        }

        private static async Task NormalizeAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Normalize(ex);
            }
        }

        private static async Task<T> NormalizeAsync<T>(Func<Task<T>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Normalize(ex);
            }
        }

        private static Exception Normalize(Exception exception)
        {
            return exception switch
            {
                ResoniteLinkDisconnectedException ex => new InteractiveUiDisconnectedException("Interactive UI transport disconnected.", ex),
                ResoniteLinkNoResponseException ex => new InteractiveUiNoResponseException("Interactive UI did not respond.", ex),
                WebSocketException ex => ex,
                TimeoutException ex => ex,
                ObjectDisposedException ex => ex,
                InvalidOperationException ex => ex,
                ArgumentException ex => ex,
                HttpRequestException ex => ex,
                _ => exception
            };
        }
    }
}
