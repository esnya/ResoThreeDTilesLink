using System.Net.WebSockets;
using ResoniteLink;

namespace ThreeDTilesLink.Core.Resonite
{
    internal sealed class ResoniteAssetImportPool : IAsyncDisposable
    {
        private readonly ImportWorker[] _workers;
        private int _nextWorkerIndex = -1;

        public ResoniteAssetImportPool(int workerCount, Func<LinkInterface> linkInterfaceFactory)
        {
            ArgumentNullException.ThrowIfNull(linkInterfaceFactory);
            if (workerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(workerCount), "Worker count must be positive.");
            }

            _workers = Enumerable.Range(0, workerCount)
                .Select(_ => new ImportWorker(linkInterfaceFactory))
                .ToArray();
        }

        public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            foreach (ImportWorker worker in _workers)
            {
                await worker.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            foreach (ImportWorker worker in _workers)
            {
                await worker.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task<AssetData> ImportMeshAsync(ImportMeshRawData importMesh, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return GetNextWorker().ExecuteAsync(
                link => link.ImportMesh(importMesh),
                timeout,
                cancellationToken);
        }

        public Task<AssetData> ImportTextureAsync(ImportTexture2DRawData importTexture, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return GetNextWorker().ExecuteAsync(
                link => link.ImportTexture(importTexture),
                timeout,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (ImportWorker worker in _workers)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
        }

        private ImportWorker GetNextWorker()
        {
            int index = Interlocked.Increment(ref _nextWorkerIndex);
            if (index < 0)
            {
                index = 0;
                _ = Interlocked.Exchange(ref _nextWorkerIndex, 0);
            }

            return _workers[index % _workers.Length];
        }

        private sealed class ImportWorker(Func<LinkInterface> linkInterfaceFactory) : IAsyncDisposable
        {
            private const int LinkRequestMaxAttempts = 2;
            private readonly Func<LinkInterface> _linkInterfaceFactory = linkInterfaceFactory;
            private readonly SemaphoreSlim _gate = new(1, 1);
            private LinkInterface _linkInterface = linkInterfaceFactory();
            private Uri? _endpoint;
            private bool _initialized;

            public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _endpoint = endpoint;
                    if (_linkInterface.IsConnected)
                    {
                        return;
                    }

                    await ConnectCoreAsync(endpoint, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _ = _gate.Release();
                }
            }

            public async Task DisconnectAsync(CancellationToken cancellationToken)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    DisposeLink();
                }
                finally
                {
                    _ = _gate.Release();
                }
            }

            public async Task<T> ExecuteAsync<T>(Func<LinkInterface, Task<T>> operation, TimeSpan timeout, CancellationToken cancellationToken)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    for (int attempt = 1; attempt <= LinkRequestMaxAttempts; attempt++)
                    {
                        LinkInterface link = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeoutSource.CancelAfter(timeout);
                            return await operation(link).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex)
                            when (!cancellationToken.IsCancellationRequested && attempt < LinkRequestMaxAttempts)
                        {
                            await ReconnectAsync(ex, cancellationToken).ConfigureAwait(false);
                        }
                        catch (TimeoutException ex) when (attempt < LinkRequestMaxAttempts)
                        {
                            await ReconnectAsync(ex, cancellationToken).ConfigureAwait(false);
                        }
                        catch (ObjectDisposedException ex) when (attempt < LinkRequestMaxAttempts)
                        {
                            await ReconnectAsync(ex, cancellationToken).ConfigureAwait(false);
                        }
                        catch (WebSocketException ex) when (attempt < LinkRequestMaxAttempts)
                        {
                            await ReconnectAsync(ex, cancellationToken).ConfigureAwait(false);
                        }
                        catch (ResoniteLinkNoResponseException ex) when (attempt < LinkRequestMaxAttempts)
                        {
                            await ReconnectAsync(ex, cancellationToken).ConfigureAwait(false);
                        }
                        catch (ResoniteLinkDisconnectedException ex) when (attempt < LinkRequestMaxAttempts)
                        {
                            await ReconnectAsync(ex, cancellationToken).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex) when (attempt < LinkRequestMaxAttempts)
                        {
                            await ReconnectAsync(ex, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    throw new InvalidOperationException("Resonite asset import retry loop exited unexpectedly.");
                }
                finally
                {
                    _ = _gate.Release();
                }
            }

            public async ValueTask DisposeAsync()
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    DisposeLink();
                }
                finally
                {
                    _ = _gate.Release();
                    _gate.Dispose();
                }
            }

            private async Task<LinkInterface> EnsureConnectedAsync(CancellationToken cancellationToken)
            {
                if (_linkInterface.IsConnected)
                {
                    return _linkInterface;
                }

                if (_endpoint is null)
                {
                    throw new ResoniteLinkDisconnectedException();
                }

                await ConnectCoreAsync(_endpoint, cancellationToken).ConfigureAwait(false);
                if (_linkInterface.IsConnected)
                {
                    return _linkInterface;
                }

                throw new ResoniteLinkDisconnectedException();
            }

            private async Task ReconnectAsync(Exception _, CancellationToken cancellationToken)
            {
                if (_endpoint is null)
                {
                    throw new ResoniteLinkDisconnectedException();
                }

                DisposeLink();
                await ConnectCoreAsync(_endpoint, cancellationToken).ConfigureAwait(false);
            }

            private async Task ConnectCoreAsync(Uri endpoint, CancellationToken cancellationToken)
            {
                try
                {
                    await _linkInterface.Connect(endpoint, cancellationToken).ConfigureAwait(false);
                    _initialized = true;
                }
                catch
                {
                    DisposeLink();
                    throw;
                }
            }

            private void DisposeLink()
            {
                try
                {
                    if (_initialized)
                    {
                        _linkInterface.Dispose();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    _initialized = false;
                    _linkInterface = _linkInterfaceFactory();
                }
            }
        }
    }
}
