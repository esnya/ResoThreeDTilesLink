using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using System.Net.WebSockets;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed partial class SelectionInputReader(
        IInteractiveUiStore interactiveUiStore,
        ILogger<SelectionInputReader> logger)
    {
        private readonly IInteractiveUiStore _interactiveUiStore = interactiveUiStore;
        private readonly ILogger<SelectionInputReader> _logger = logger;

        internal async Task<SelectionInputSnapshot> ReadAsync(InteractiveUiBinding inputBinding, CancellationToken cancellationToken)
        {
            SelectionInputValues? rawValues = await TryReadRawInteractiveUiValuesAsync(inputBinding, cancellationToken).ConfigureAwait(false);
            SelectionInputValues? values = NormalizeInteractiveInputValues(rawValues);
            string? searchText = await TryReadInteractiveUiSearchAsync(inputBinding, cancellationToken).ConfigureAwait(false);
            return new SelectionInputSnapshot(
                searchText,
                values,
                HasInvalidValues: rawValues is not null && values is null);
        }

        internal async Task<string?> TryReadInteractiveUiSearchAsync(InteractiveUiBinding inputBinding, CancellationToken cancellationToken)
        {
            try
            {
                return NormalizeSearchText(await _interactiveUiStore.ReadInteractiveUiSearchAsync(inputBinding, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InteractiveUiDisconnectedException)
            {
                throw;
            }
            catch (InteractiveUiNoResponseException ex)
            {
                Log.SearchReadNoResponse(_logger, ex);
                return null;
            }
            catch (ObjectDisposedException ex)
            {
                Log.SearchReadWarning(_logger, ex);
                return null;
            }
            catch (TimeoutException ex)
            {
                Log.SearchReadWarning(_logger, ex);
                return null;
            }
            catch (WebSocketException ex)
            {
                Log.SearchReadWarning(_logger, ex);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                Log.SearchReadWarning(_logger, ex);
                return null;
            }
        }

        internal async Task<SelectionInputValues?> TryReadInteractiveUiValuesAsync(InteractiveUiBinding inputBinding, CancellationToken cancellationToken)
        {
            SelectionInputValues? values = await TryReadRawInteractiveUiValuesAsync(inputBinding, cancellationToken).ConfigureAwait(false);
            return NormalizeInteractiveInputValues(values);
        }

        private async Task<SelectionInputValues?> TryReadRawInteractiveUiValuesAsync(InteractiveUiBinding inputBinding, CancellationToken cancellationToken)
        {
            try
            {
                return await _interactiveUiStore.ReadInteractiveUiValuesAsync(inputBinding, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InteractiveUiDisconnectedException)
            {
                throw;
            }
            catch (InteractiveUiNoResponseException ex)
            {
                Log.ValuesReadNoResponse(_logger, ex);
                return null;
            }
            catch (ObjectDisposedException ex)
            {
                Log.ValuesReadWarning(_logger, ex);
                return null;
            }
            catch (TimeoutException ex)
            {
                Log.ValuesReadWarning(_logger, ex);
                return null;
            }
            catch (WebSocketException ex)
            {
                Log.ValuesReadWarning(_logger, ex);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                Log.ValuesReadWarning(_logger, ex);
                return null;
            }
        }

        private static bool IsValidRange(double rangeM)
        {
            return double.IsFinite(rangeM) && rangeM > 0d;
        }

        private static bool IsValidLatitude(double latitude)
        {
            return double.IsFinite(latitude) && latitude >= -90d && latitude <= 90d;
        }

        private static bool IsValidLongitude(double longitude)
        {
            return double.IsFinite(longitude) && longitude >= -180d && longitude <= 180d;
        }

        private static SelectionInputValues? NormalizeInteractiveInputValues(SelectionInputValues? values)
        {
            if (values is null ||
                !IsValidLatitude(values.Latitude) ||
                !IsValidLongitude(values.Longitude) ||
                !IsValidRange(values.RangeM))
            {
                return null;
            }

            return values;
        }

        internal static bool HasMeaningfulChange(SelectionInputValues? previous, SelectionInputValues current)
        {
            ArgumentNullException.ThrowIfNull(current);
            return previous is null ||
                System.Math.Abs(previous.Latitude - current.Latitude) > 1e-5f ||
                System.Math.Abs(previous.Longitude - current.Longitude) > 1e-5f ||
                System.Math.Abs(previous.RangeM - current.RangeM) > 0.1f;
        }

        internal static void ValidateIntervals(WatchOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.PollInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Poll interval must be positive.");
            }

            if (options.Debounce < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Debounce must be zero or positive.");
            }

            if (options.Throttle < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Throttle must be zero or positive.");
            }
        }

        private static string? NormalizeSearchText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            return input.Trim();
        }

        private static partial class Log
        {
            [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Interactive input search read returned no response.")]
            public static partial void SearchReadNoResponse(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to read interactive input search.")]
            public static partial void SearchReadWarning(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Selection input read returned no response.")]
            public static partial void ValuesReadNoResponse(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to read selection input values.")]
            public static partial void ValuesReadWarning(ILogger logger, Exception exception);
        }
    }
}
