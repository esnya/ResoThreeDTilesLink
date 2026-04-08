using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using ResoniteLink;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Tests
{
    internal sealed class SessionPool : IAsyncDisposable
    {
        private readonly WorkerLease[] _workers;

        internal sealed class SessionPoolConnectHooks
        {
            public Func<ResoniteSession> SessionFactory { get; set; } = CreateSession;

            public Func<ResoniteSession, string, int, CancellationToken, Task> ConnectSession { get; set; } =
                static (session, host, port, cancellationToken) => session.ConnectAsync(host, port, cancellationToken);

            public Func<ResoniteSession, Task> CleanupSession { get; set; } = static session =>
                TryDisposeIgnoringFailureAsync(session);
        }

        private SessionPool(string host, int port, ResoniteSession controller, WorkerLease[] workers)
        {
            Host = host;
            Port = port;
            Controller = controller;
            _workers = workers;
        }

        public string Host { get; }

        public int Port { get; }

        public ResoniteSession Controller { get; }

        [SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The created sessions are owned by SessionPool and disposed in DisposeAsync.")]
        public static async Task<SessionPool> ConnectAsync(string host, int port, int poolSize)
        {
            return await ConnectAsync(host, port, poolSize, new SessionPoolConnectHooks()).ConfigureAwait(false);
        }

        internal static async Task<SessionPool> ConnectAsync(
            string host,
            int port,
            int poolSize,
            SessionPoolConnectHooks hooks)
        {
            ResoniteSession controller = hooks.SessionFactory();
            var workers = new WorkerLease[poolSize];
            int connectedWorkerCount = 0;
            try
            {
                await hooks.ConnectSession(controller, host, port, CancellationToken.None).ConfigureAwait(false);

                for (int i = 0; i < workers.Length; i++)
                {
                    ResoniteSession session = hooks.SessionFactory();
                    try
                    {
                        await hooks.ConnectSession(session, host, port, CancellationToken.None).ConfigureAwait(false);
                        workers[connectedWorkerCount++] = new WorkerLease(session);
                    }
                    catch
                    {
                        await hooks.CleanupSession(session).ConfigureAwait(false);
                        throw;
                    }
                }

                return new SessionPool(host, port, controller, workers);
            }
            catch
            {
                for (int i = 0; i < connectedWorkerCount; i++)
                {
                    await hooks.CleanupSession(workers[i].Session).ConfigureAwait(false);
                }

                await hooks.CleanupSession(controller).ConfigureAwait(false);
                throw;
            }
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "Cleanup path intentionally swallows disposal exceptions to preserve the original connection failure.")]
        private static async Task TryDisposeIgnoringFailureAsync(ResoniteSession session)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        public WorkerLease GetSingleWorker()
        {
            return _workers[0];
        }

        public WorkerLease GetWorker(int requestIndex)
        {
            int index = requestIndex % _workers.Length;
            if (index < 0)
            {
                index += _workers.Length;
            }

            return _workers[index];
        }

        [SuppressMessage(
            "Reliability",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "DisposeAsync aggregates worker teardown failures and rethrows them after all sessions are closed.")]
        public async ValueTask DisposeAsync()
        {
            List<Exception>? failures = null;

            foreach (WorkerLease worker in _workers)
            {
                try
                {
                    await worker.Session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures ??= [];
                    failures.Add(ex);
                }
            }

            try
            {
                await Controller.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(ex);
            }

            if (failures is { Count: > 0 })
            {
                throw new AggregateException(failures);
            }
        }

        public static ResoniteSession CreateSession()
        {
#pragma warning disable CA2000
            var link = new LinkInterface();
#pragma warning restore CA2000
            return new ResoniteSession(link, NullLogger<ResoniteSession>.Instance);
        }

        internal sealed record WorkerLease(ResoniteSession Session);
    }
}
