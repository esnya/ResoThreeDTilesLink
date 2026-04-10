using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Pipeline;

namespace ThreeDTilesLink.App
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The generic host instantiates this hosted service via DI.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The injected TextWriter is shared process output and is not owned by the hosted service.")]
    internal sealed class StreamCommandHostedService(
        StreamCommandOptions options,
        ITileSelectionService tileSelectionService,
        IGeoReferenceResolver geoReferenceResolver,
        IOptions<GoogleMapsOptions> googleMapsOptions,
        TextWriter output,
        CommandCompletion completion,
        IHostApplicationLifetime lifetime) : BackgroundService
    {
        private readonly StreamCommandOptions _options = options;
        private readonly ITileSelectionService _tileSelectionService = tileSelectionService;
        private readonly IGeoReferenceResolver _geoReferenceResolver = geoReferenceResolver;
        private readonly IOptions<GoogleMapsOptions> _googleMapsOptions = googleMapsOptions;
        private readonly TextWriter _output = output;
        private readonly CommandCompletion _completion = completion;
        private readonly IHostApplicationLifetime _lifetime = lifetime;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                RunSummary summary = await _tileSelectionService.RunAsync(
                    StreamCommandHandler.CreateRequest(
                        _options,
                        _googleMapsOptions.Value.ApiKey,
                        _geoReferenceResolver),
                    stoppingToken).ConfigureAwait(false);

                await _output.WriteLineAsync(
                    $"CandidateTiles={summary.CandidateTiles} ProcessedTiles={summary.ProcessedTiles} StreamedMeshes={summary.StreamedMeshes} FailedTiles={summary.FailedTiles}")
                    .ConfigureAwait(false);
                _completion.TrySetExitCode(0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _completion.TrySetExitCode(0);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                throw;
            }
            finally
            {
                _lifetime.StopApplication();
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The generic host instantiates this hosted service via DI.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The injected TextWriter is shared process output and is not owned by the hosted service.")]
    internal sealed class InteractiveCommandHostedService(
        InteractiveCommandOptions options,
        InteractiveRunSupervisor interactiveRunSupervisor,
        IOptions<GoogleMapsOptions> googleMapsOptions,
        TextWriter output,
        CommandCompletion completion,
        IHostApplicationLifetime lifetime) : BackgroundService
    {
        private readonly InteractiveCommandOptions _options = options;
        private readonly InteractiveRunSupervisor _interactiveRunSupervisor = interactiveRunSupervisor;
        private readonly IOptions<GoogleMapsOptions> _googleMapsOptions = googleMapsOptions;
        private readonly TextWriter _output = output;
        private readonly CommandCompletion _completion = completion;
        private readonly IHostApplicationLifetime _lifetime = lifetime;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _output.WriteLineAsync(
                    $"Interactive mode started. Input=session root slot values (Latitude/Longitude/Range/Search). Poll={_options.PollIntervalMs}ms Debounce={_options.DebounceMs}ms Throttle={_options.ThrottleMs}ms. Press Ctrl+C to stop.")
                    .ConfigureAwait(false);
                await _interactiveRunSupervisor.RunAsync(
                    InteractiveCommandHandler.CreateRequest(_options, _googleMapsOptions.Value.ApiKey),
                    stoppingToken).ConfigureAwait(false);
                _completion.TrySetExitCode(0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _completion.TrySetExitCode(0);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                throw;
            }
            finally
            {
                _lifetime.StopApplication();
            }
        }
    }
}
