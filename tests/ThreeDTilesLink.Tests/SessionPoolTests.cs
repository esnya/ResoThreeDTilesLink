using Microsoft.Extensions.Logging.Abstractions;
using ResoniteLink;
using FluentAssertions;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Tests
{
    public sealed class SessionPoolTests
    {
        [Fact]
        public async Task ConnectAsync_PartialWorkerConnectionFailure_CleansUpConnectedSessions()
        {
            var hooks = new SessionPool.SessionPoolConnectHooks();
            Func<ResoniteSession> defaultSessionFactory = hooks.SessionFactory;

            List<ResoniteSession> createdSessions = [];
            List<ResoniteSession> connectedSessions = [];
            List<ResoniteSession> cleanedSessions = [];
            int connectCallCount = 0;
            ResoniteSession? failedSession = null;

            Func<ResoniteSession> createSession = () =>
            {
                ResoniteSession session = defaultSessionFactory();
                createdSessions.Add(session);
                return session;
            };

            Func<ResoniteSession, string, int, CancellationToken, Task> connectSession =
                (session, _, _, _) =>
                {
                    connectedSessions.Add(session);
                    int call = Interlocked.Increment(ref connectCallCount);
                    if (call == 3)
                    {
                        failedSession = session;
                        return Task.FromException(new InvalidOperationException("Simulated worker connection failure"));
                    }

                    return Task.CompletedTask;
                };

            Func<ResoniteSession, Task> cleanupSession = session =>
            {
                cleanedSessions.Add(session);
                return Task.CompletedTask;
            };

            hooks.SessionFactory = createSession;
            hooks.ConnectSession = connectSession;
            hooks.CleanupSession = cleanupSession;

            Func<Task> act = () => SessionPool.ConnectAsync("localhost", 49379, 2, hooks);

            try
            {
                InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(act);

                _ = exception.Message.Should().Be("Simulated worker connection failure");
                _ = createdSessions.Should().HaveCount(3);
                _ = connectedSessions.Should().HaveCount(3);
                _ = cleanedSessions.Should().HaveCount(3);
                _ = cleanedSessions.Distinct().Should().HaveCount(3);
                _ = cleanedSessions.Should().Contain(createdSessions[0]);
                _ = cleanedSessions.Should().Contain(createdSessions[1]);
                _ = cleanedSessions.Should().Contain(createdSessions[2]);
                _ = failedSession.Should().NotBeNull();
                _ = cleanedSessions.Should().Contain(failedSession!);
            }
            finally
            {
                foreach (ResoniteSession session in createdSessions)
                {
                    try
                    {
                        await session.DisposeAsync().AsTask();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
    }
}
