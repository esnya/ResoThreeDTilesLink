// Diagnostic console tool; localized resources are unnecessary here.
#pragma warning disable CA1303
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    Options options = Options.Parse(args);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSec));
    using var socket = new ClientWebSocket();

    await socket.ConnectAsync(new Uri($"ws://{options.Host}:{options.Port}/"), cts.Token).ConfigureAwait(false);

    try
    {
        JsonObject rootSlot = await SendRequestAsync(
            socket,
            new JsonObject
            {
                ["$type"] = "getSlot",
                ["slotId"] = "Root",
                ["includeComponentData"] = false,
                ["depth"] = 1
            },
            cts.Token).ConfigureAwait(false);

        JsonArray children = rootSlot["data"]?["children"]?.AsArray() ?? [];
        List<string> removed = [];

        foreach (JsonNode? child in children)
        {
            if (child is not JsonObject slot)
            {
                continue;
            }

            string? slotId = slot["id"]?.GetValue<string>();
            string? slotName = slot["name"]?["value"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(slotId) || string.IsNullOrWhiteSpace(slotName))
            {
                continue;
            }

            if (!slotName.StartsWith("3DTilesLink Session ", StringComparison.Ordinal))
            {
                continue;
            }

            _ = await SendRequestAsync(
                socket,
                new JsonObject
                {
                    ["$type"] = "removeSlot",
                    ["slotId"] = slotId
                },
                cts.Token).ConfigureAwait(false);
            removed.Add($"{slotName} [{slotId}]");
        }

        if (removed.Count == 0)
        {
            Console.WriteLine("No 3DTilesLink session roots found.");
        }
        else
        {
            foreach (string entry in removed)
            {
                Console.WriteLine($"Removed {entry}");
            }
        }
    }
    finally
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None).ConfigureAwait(false);
        }
    }

    return 0;
}

static async Task<JsonObject> SendRequestAsync(ClientWebSocket socket, JsonObject payload, CancellationToken cancellationToken)
{
    string messageId = Guid.NewGuid().ToString("N");
    payload["messageId"] = messageId;
    byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

    while (true)
    {
        JsonObject response = await ReceiveObjectAsync(socket, cancellationToken).ConfigureAwait(false);
        string? sourceMessageId = response["sourceMessageId"]?.GetValue<string>();
        if (!string.Equals(sourceMessageId, messageId, StringComparison.Ordinal))
        {
            continue;
        }

        bool success = response["success"]?.GetValue<bool>() ?? false;
        if (!success)
        {
            string? errorInfo = response["errorInfo"]?.ToJsonString();
            throw new InvalidOperationException($"ResoniteLink request failed: {errorInfo}");
        }

        return response;
    }
}

static async Task<JsonObject> ReceiveObjectAsync(ClientWebSocket socket, CancellationToken cancellationToken)
{
    byte[] buffer = new byte[8192];
    using var stream = new MemoryStream();

    while (socket.State == WebSocketState.Open)
    {
        stream.SetLength(0);
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Socket closed before a matching response was received.");
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                continue;
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
        }
        while (!result.EndOfMessage);

        if (result.MessageType != WebSocketMessageType.Text)
        {
            continue;
        }

        JsonNode? parsed = JsonNode.Parse(stream.ToArray());
        if (parsed is JsonObject obj)
        {
            return obj;
        }
    }

    throw new TimeoutException("Timed out waiting for ResoniteLink response.");
}

internal sealed record Options(string Host, int Port, int TimeoutSec)
{
    public static Options Parse(IReadOnlyList<string> args)
    {
        string host = "localhost";
        int port = 49379;
        int timeoutSec = 15;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--host":
                    host = ReadRequiredValue(args, ref i, arg);
                    break;
                case "--port":
                    port = int.Parse(ReadRequiredValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--timeout-sec":
                    timeoutSec = int.Parse(ReadRequiredValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--help":
                case "-h":
                    PrintHelpAndExit(0);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument: {arg}");
            }
        }

        return new Options(host, port, timeoutSec);
    }

    private static string ReadRequiredValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new InvalidOperationException($"Missing value for {optionName}");
        }

        index++;
        return args[index];
    }

    private static void PrintHelpAndExit(int exitCode)
    {
        Console.WriteLine("Usage: dotnet run --project tools/ResoniteSessionCleanup -- [options]");
        Console.WriteLine("  --host <host>         Resonite Link host. Default: localhost");
        Console.WriteLine("  --port <port>         Resonite Link port. Default: 49379");
        Console.WriteLine("  --timeout-sec <sec>   Request timeout. Default: 15");
        Environment.Exit(exitCode);
    }
}
