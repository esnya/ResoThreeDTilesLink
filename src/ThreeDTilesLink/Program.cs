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
            RootCommandKind.Stream => await CommandHost.RunAsync(
                route.Arguments,
                StreamCommandLine.Parse,
                StreamCommandHandler.RunAsync,
                Console.Out,
                CancellationToken.None).ConfigureAwait(false),
            RootCommandKind.Interactive => await CommandHost.RunAsync(
                route.Arguments,
                InteractiveCommandLine.Parse,
                ConsoleInteractiveHost.RunAsync,
                Console.Out,
                CancellationToken.None).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported command: {route.Command}")
        };
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
        return 1;
    }
}

static async Task WriteOutputAsync(string output, bool writeToError)
{
    await CommandHost.WriteOutputAsync(Console.Out, output, writeToError).ConfigureAwait(false);
}
