using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed partial class SelectionInputReader(
        IInteractiveInputStore interactiveInputStore,
        ILogger<SelectionInputReader> logger)
    {
        private readonly IInteractiveInputStore _interactiveInputStore = interactiveInputStore;
        private readonly ILogger<SelectionInputReader> _logger = logger;

        internal async Task<SelectionInputSnapshot> ReadAsync(InteractiveInputBinding inputBinding, CancellationToken cancellationToken)
        {
            SelectionInputValues? values = await TryReadInteractiveInputValuesAsync(inputBinding, cancellationToken).ConfigureAwait(false);
            string? searchText = await TryReadInteractiveInputSearchAsync(inputBinding, cancellationToken).ConfigureAwait(false);
            return new SelectionInputSnapshot(searchText, values);
        }

        internal async Task<string?> TryReadInteractiveInputSearchAsync(InteractiveInputBinding inputBinding, CancellationToken cancellationToken)
        {
            try
            {
                return NormalizeSearchText(await _interactiveInputStore.ReadInteractiveInputSearchAsync(inputBinding, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                throw;
            }
            catch (ResoniteLinkNoResponseException ex)
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

        internal async Task<SelectionInputValues?> TryReadInteractiveInputValuesAsync(InteractiveInputBinding inputBinding, CancellationToken cancellationToken)
        {
            try
            {
                SelectionInputValues? values = await _interactiveInputStore.ReadInteractiveInputValuesAsync(inputBinding, cancellationToken).ConfigureAwait(false);
                if (values is null || !IsValidRange(values.RangeM))
                {
                    return null;
                }

                return values;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                throw;
            }
            catch (ResoniteLinkNoResponseException ex)
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
