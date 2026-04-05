using DotNetEnv;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;
using ThreeDTilesLink.Core.Runtime;

int exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

[SuppressMessage(
    "Reliability",
    "CA1031:DoNotCatchGeneralExceptionTypes",
    Justification = "The top-level command entrypoint converts any unexpected failure into a user-visible message and non-zero exit code.")]
static async Task<int> RunAsync(string[] args)
{
    try
    {
        _ = Env.TraversePath().NoClobber().Load();

        CommandInvocation<RootCommandRoute> rootInvocation = RootCommandLine.Parse(args);
        if (!rootInvocation.ShouldRun)
        {
            await WriteOutputAsync(rootInvocation.Output, rootInvocation.WriteToError).ConfigureAwait(false);
            return rootInvocation.ExitCode;
        }

        RootCommandRoute route = rootInvocation.Options!;
        return route.Command switch
        {
            RootCommandKind.Stream => await RunStreamAsync(route.Arguments).ConfigureAwait(false),
            RootCommandKind.Interactive => await RunInteractiveAsync(route.Arguments).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported command: {route.Command}")
        };
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
        return 1;
    }
}

static async Task<int> RunStreamAsync(IReadOnlyList<string> args)
{
    CommandInvocation<StreamCommandOptions> invocation = StreamCommandLine.Parse(args);
    if (!invocation.ShouldRun)
    {
        await WriteOutputAsync(invocation.Output, invocation.WriteToError).ConfigureAwait(false);
        return invocation.ExitCode;
    }

    StreamCommandOptions options = invocation.Options!;
    string apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? string.Empty;

    using ILoggerFactory loggerFactory = CreateLoggerFactory(options.LogLevel);
    var runtime = new TileStreamingRuntime(
        loggerFactory,
        TimeSpan.FromSeconds(options.TimeoutSec),
        options.ContentWorkers);
    await using (runtime.ConfigureAwait(false))
    {
        return await StreamCommandHandler.RunAsync(options, runtime, apiKey, Console.Out, CancellationToken.None).ConfigureAwait(false);
    }
}

static async Task<int> RunInteractiveAsync(IReadOnlyList<string> args)
{
    CommandInvocation<InteractiveCommandOptions> invocation = InteractiveCommandLine.Parse(args);
    if (!invocation.ShouldRun)
    {
        await WriteOutputAsync(invocation.Output, invocation.WriteToError).ConfigureAwait(false);
        return invocation.ExitCode;
    }

    InteractiveCommandOptions options = invocation.Options!;
    string apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? string.Empty;

    using ILoggerFactory loggerFactory = CreateLoggerFactory(options.LogLevel);
    var runtime = new TileStreamingRuntime(
        loggerFactory,
        TimeSpan.FromSeconds(options.TimeoutSec),
        options.ContentWorkers);
    await using (runtime.ConfigureAwait(false))
    {
        return await InteractiveCommandHandler.RunAsync(options, runtime, apiKey, Console.Out, CancellationToken.None).ConfigureAwait(false);
    }
}

static ILoggerFactory CreateLoggerFactory(LogLevel logLevel)
{
    return LoggerFactory.Create(builder =>
    {
        _ = builder
            .SetMinimumLevel(logLevel)
            .AddSimpleConsole(options =>
            {
                options.IncludeScopes = false;
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
    });
}

static async Task WriteOutputAsync(string output, bool writeToError)
{
    if (string.IsNullOrWhiteSpace(output))
    {
        return;
    }

    if (writeToError)
    {
        await Console.Error.WriteLineAsync(output).ConfigureAwait(false);
        return;
    }

    await Console.Out.WriteLineAsync(output).ConfigureAwait(false);
}
