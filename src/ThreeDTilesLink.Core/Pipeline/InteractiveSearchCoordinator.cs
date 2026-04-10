using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Google;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class InteractiveSearchCoordinator(
        IInteractiveInputStore interactiveInputStore,
        ISearchResolver searchResolver,
        IClock clock,
        ILogger<InteractiveSearchCoordinator> logger)
    {
        private readonly IInteractiveInputStore _interactiveInputStore = interactiveInputStore;
        private readonly ISearchResolver _searchResolver = searchResolver;
        private readonly IClock _clock = clock;
        private readonly ILogger<InteractiveSearchCoordinator> _logger = logger;

        [SuppressMessage(
            "Design",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Interactive search resolution deliberately converts known transport/protocol/watch update failures into a retryable state.")]
        internal async Task<InteractiveLoopState> ResolveSearchAsync(
            InteractiveLoopState state,
            InteractiveRunRequest options,
            ResolveSearchAction action,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                InteractiveRunSupervisor.Log.SearchIgnored(_logger, action.SearchText);
                return MarkSearchHandled(state, action.SearchText);
            }

            try
            {
                LocationSearchResult? result = await _searchResolver.SearchAsync(options.ApiKey, action.SearchText, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    InteractiveRunSupervisor.Log.SearchNoResult(_logger, action.SearchText);
                    return MarkSearchHandled(state, action.SearchText);
                }

                try
                {
                    await _interactiveInputStore.UpdateInteractiveInputCoordinatesAsync(state.InputBinding!, result.Latitude, result.Longitude, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsRetryableSearchFailure(ex))
                {
                    InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                    return RequeueSearch(state, action.SearchText);
                }

                InteractiveRunSupervisor.Log.SearchResolved(
                    _logger,
                    action.SearchText,
                    result.FormattedAddress,
                    result.Latitude,
                    result.Longitude);
                return state with
                {
                    AwaitingResolvedCoordinates = result,
                    PendingValues = null,
                    PendingValuesChangedAt = null,
                    LastResolvedSearch = action.SearchText
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryableSearchFailure(ex))
            {
                InteractiveRunSupervisor.Log.SearchResolutionFailed(_logger, ex, action.SearchText);
                return RequeueSearch(state, action.SearchText);
            }
        }

        private InteractiveLoopState RequeueSearch(
            InteractiveLoopState state,
            string searchText)
        {
            return state with
            {
                PendingSearch = searchText,
                PendingSearchChangedAt = _clock.UtcNow
            };
        }

        private static InteractiveLoopState MarkSearchHandled(InteractiveLoopState state, string searchText)
        {
            return state with
            {
                PendingSearch = null,
                PendingSearchChangedAt = null,
                LastResolvedSearch = searchText
            };
        }

        private static bool IsRetryableSearchFailure(Exception exception)
        {
            return exception is ArgumentException
                or ResoniteLinkNoResponseException
                or ResoniteLinkDisconnectedException
                or HttpRequestException
                or TimeoutException
                or ObjectDisposedException
                or WebSocketException;
        }
    }
}
