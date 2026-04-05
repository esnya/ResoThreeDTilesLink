// Thin launcher around the official REPL controller so scripts can pass host/port.
#pragma warning disable CA1303
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using ResoniteLink;

return await RunAsync(args).ConfigureAwait(false);

[SuppressMessage(
    "Reliability",
    "CA1031:DoNotCatchGeneralExceptionTypes",
    Justification = "This tool converts unexpected interactive failures into a user-visible exit code.")]
static async Task<int> RunAsync(string[] args)
{
    Uri targetUrl = args.Length switch
    {
        0 => PromptForTarget(),
        1 => ParseSingleArgument(args[0]),
        2 => BuildHostPortTarget(args[0], args[1]),
        _ => throw new InvalidOperationException("Usage: ResoniteRepl [port|ws://host:port|host port]")
    };

    if (!string.Equals(targetUrl.Scheme, "ws", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Scheme must be ws (websocket).");
    }

    try
    {
        await Console.Out.WriteLineAsync($"Connecting to {targetUrl}...").ConfigureAwait(false);

        using var link = new LinkInterface();
        await link.Connect(targetUrl, CancellationToken.None).ConfigureAwait(false);

        await Console.Out.WriteLineAsync("Connected.").ConfigureAwait(false);
        var repl = new REPL_Controller(link, new ConsoleCommandIO());
        await repl.RunLoop().ConfigureAwait(false);
        return 0;
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"Error: {ex}").ConfigureAwait(false);
        return 2;
    }
}

static Uri PromptForTarget()
{
    Console.Write("Connect to (localhost port or ws:// URL): ");
    string input = (Console.ReadLine() ?? string.Empty).Trim();
    return ParseSingleArgument(input);
}

static Uri ParseSingleArgument(string argument)
{
    if (int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
    {
        return BuildHostPortTarget("localhost", port.ToString(CultureInfo.InvariantCulture));
    }

    if (Uri.TryCreate(argument, UriKind.Absolute, out Uri? targetUrl))
    {
        return targetUrl;
    }

    throw new InvalidOperationException("Failed to parse URL.");
}

static Uri BuildHostPortTarget(string host, string portText)
{
    if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
    {
        throw new InvalidOperationException("Port must be an integer.");
    }

    return new Uri($"ws://{host}:{port}/");
}

internal sealed class ConsoleCommandIO : ICommandIO
{
    public Task<string> ReadCommand()
    {
        return Task.FromResult(Console.ReadLine() ?? string.Empty);
    }

    public Task PrintPrompt(string prompt)
    {
        ConsoleColor foregroundColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"{prompt}: ");
        Console.ForegroundColor = foregroundColor;
        return Task.CompletedTask;
    }

    public Task Print(string message)
    {
        Console.Write(message);
        return Task.CompletedTask;
    }

    public Task PrintLine(string message)
    {
        Console.WriteLine(message);
        return Task.CompletedTask;
    }

    public Task PrintError(string message)
    {
        ConsoleColor foregroundColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = foregroundColor;
        return Task.CompletedTask;
    }
}
