using Microsoft.Extensions.Hosting;
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
        TileSourceOptions tileSource,
        TextWriter output,
        CommandCompletion completion,
        IHostApplicationLifetime lifetime) : BackgroundService
    {
        private readonly StreamCommandOptions _options = options;
        private readonly ITileSelectionService _tileSelectionService = tileSelectionService;
        private readonly IGeoReferenceResolver _geoReferenceResolver = geoReferenceResolver;
        private readonly TileSourceOptions _tileSource = tileSource;
        private readonly TextWriter _output = output;
        private readonly CommandCompletion _completion = completion;
        private readonly IHostApplicationLifetime _lifetime = lifetime;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                RunSummary summary = await _tileSelectionService.RunAsync(
                    CommandRequestFactory.CreateStreamRequest(
                        _options,
                        _tileSource,
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
        TileSourceOptions tileSource,
        SearchOptions searchOptions,
        TextWriter output,
        CommandCompletion completion,
        IHostApplicationLifetime lifetime) : BackgroundService
    {
        private readonly InteractiveCommandOptions _options = options;
        private readonly InteractiveRunSupervisor _interactiveRunSupervisor = interactiveRunSupervisor;
        private readonly TileSourceOptions _tileSource = tileSource;
        private readonly SearchOptions _searchOptions = searchOptions;
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
                    CommandRequestFactory.CreateInteractiveRequest(_options, _tileSource, _searchOptions),
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
