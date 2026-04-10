using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Pipeline
{
    internal sealed class InteractiveActionApplier(
        ITileSelectionService tileRunCoordinator,
        IResoniteSession resoniteSession,
        IInteractiveInputStore interactiveInputStore,
        ISearchResolver searchResolver,
        ICoordinateTransformer coordinateTransformer,
        IClock clock,
        ILoggerFactory loggerFactory)
    {
        private readonly ICoordinateTransformer _coordinateTransformer = coordinateTransformer;
        private readonly InteractiveSessionManager _sessionManager = new(
            tileRunCoordinator,
            resoniteSession,
            loggerFactory.CreateLogger<InteractiveSessionManager>());
        private readonly InteractiveSearchCoordinator _searchCoordinator = new(
            interactiveInputStore,
            searchResolver,
            clock,
            loggerFactory.CreateLogger<InteractiveSearchCoordinator>());

        internal Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            return _sessionManager.ConnectAsync(host, port, cancellationToken);
        }

        internal Vector3d GeographicToEcef(double latitude, double longitude, double height)
        {
            return _coordinateTransformer.GeographicToEcef(latitude, longitude, height);
        }

        internal Vector3d EcefToEnu(Vector3d ecef, GeoReference reference)
        {
            return _coordinateTransformer.EcefToEnu(ecef, reference);
        }

        internal async Task<InteractiveLoopState> FinalizeCompletedRunAsync(
            InteractiveLoopState state,
            CancellationToken cancellationToken)
        {
            return await _sessionManager.FinalizeCompletedRunAsync(state, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<InteractiveLoopState> ApplyAsync(
            InteractiveLoopState state,
            IEnumerable<InteractiveAction> actions,
            InteractiveRunRequest options,
            CancellationToken cancellationToken)
        {
            foreach (InteractiveAction action in actions)
            {
                state = action switch
                {
                    ResolveSearchAction resolve => await _searchCoordinator.ResolveSearchAsync(state, options, resolve, cancellationToken).ConfigureAwait(false),
                    CancelActiveRunAction => await _sessionManager.CancelActiveRunAsync(state).ConfigureAwait(false),
                    StartRunAction start => await _sessionManager.StartRunAsync(state, options, start, cancellationToken).ConfigureAwait(false),
                    _ => state
                };
            }

            return state;
        }

        internal async Task<InteractiveLoopState> DisconnectAsync(
            InteractiveLoopState state,
            CancellationToken cancellationToken)
        {
            return await _sessionManager.DisconnectAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }
}
