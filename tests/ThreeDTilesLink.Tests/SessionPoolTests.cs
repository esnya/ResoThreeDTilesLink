using System.Reflection;
using FluentAssertions;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Tests
{
    public sealed class SessionPoolTests
    {
        [Fact]
        public async Task ConnectAsync_PartialWorkerConnectionFailure_CleansUpConnectedSessions()
        {
            Type sessionPoolType = GetSessionPoolType();
            Type hooksType = GetSessionPoolHooksType(sessionPoolType);

            MethodInfo connectAsyncMethod = sessionPoolType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                .Single(static method => method.Name == "ConnectAsync" && method.GetParameters().Length == 4);

            PropertyInfo sessionFactoryProperty = hooksType.GetProperty("SessionFactory", BindingFlags.Public | BindingFlags.Instance)!;
            PropertyInfo connectSessionProperty = hooksType.GetProperty("ConnectSession", BindingFlags.Public | BindingFlags.Instance)!;
            PropertyInfo cleanupSessionProperty = hooksType.GetProperty("CleanupSession", BindingFlags.Public | BindingFlags.Instance)!;

            object hooks = Activator.CreateInstance(hooksType)!;

            var defaultSessionFactory = (Func<ResoniteSession>)sessionFactoryProperty.GetValue(hooks)!;

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

            sessionFactoryProperty.SetValue(hooks, createSession);
            connectSessionProperty.SetValue(hooks, connectSession);
            cleanupSessionProperty.SetValue(hooks, cleanupSession);

            Func<Task> act = () =>
            {
                object? result = connectAsyncMethod.Invoke(
                    null,
                    ["localhost", 49379, 2, hooks]);
                return (Task)result!;
            };

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

        private static Type GetSessionPoolType()
        {
            const string assemblyFileName = "ResonitePoolExperiment.dll";
            const string typeName = "SessionPool";

            string searchRoot = FindRepositoryRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
            string? assemblyPath = Path.Combine(
                searchRoot,
                "tools",
                "ResonitePoolExperiment",
                "bin",
                "Debug",
                "net10.0",
                assemblyFileName);
            if (!File.Exists(assemblyPath))
            {
                assemblyPath = Directory.EnumerateFiles(searchRoot, assemblyFileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
            }

            if (assemblyPath is null)
            {
                throw new FileNotFoundException(
                    $"Failed to locate {assemblyFileName} from {searchRoot}.");
            }

            Type? sessionPoolType = Assembly.LoadFrom(assemblyPath).GetType(typeName);
            return sessionPoolType ?? throw new TypeLoadException($"Type {typeName} was not found in {assemblyPath}.");
        }

        private static Type GetSessionPoolHooksType(Type sessionPoolType)
        {
            return sessionPoolType.GetNestedType("SessionPoolConnectHooks", BindingFlags.NonPublic)!;
        }

        private static string? FindRepositoryRoot(string startDirectory)
        {
            for (DirectoryInfo? current = new(startDirectory); current is not null; current = current.Parent)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                    || File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
                {
                    return current.FullName;
                }
            }

            return null;
        }
    }
}
