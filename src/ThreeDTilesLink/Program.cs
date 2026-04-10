using DotNetEnv;
using System.Diagnostics.CodeAnalysis;
using ThreeDTilesLink.Core.App;
using ThreeDTilesLink.Core.CommandLine;

int exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

[SuppressMessage(
    "Reliability",
    "CA1031:DoNotCatchGeneralExceptionTypes",
    Justification = "The top-level command entrypoint converts any unexpected failure into a user-visible message and non-zero exit code.")]
static async Task<int> RunAsync(string[] args)
{
    using var appCts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        _ = appCts.CancelAsync();
    };

    try
    {
        Console.CancelKeyPress += cancelHandler;
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
            RootCommandKind.Stream => await ThreeDTilesLink.CommandHost.RunAsync(
                route.Arguments,
                StreamCommandLine.Parse,
                StreamCommandHandler.RunAsync,
                Console.Out,
                appCts.Token).ConfigureAwait(false),
            RootCommandKind.Interactive => await ThreeDTilesLink.CommandHost.RunAsync(
                route.Arguments,
                InteractiveCommandLine.Parse,
                ConsoleInteractiveHost.RunAsync,
                Console.Out,
                appCts.Token).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported command: {route.Command}")
        };
    }
    catch (OperationCanceledException) when (appCts.IsCancellationRequested)
    {
        return 0;
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task WriteOutputAsync(string output, bool writeToError)
{
    await ThreeDTilesLink.CommandHost.WriteOutputAsync(Console.Out, output, writeToError).ConfigureAwait(false);
}
