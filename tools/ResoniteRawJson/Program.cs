// Diagnostic console tool; localized resources are unnecessary here.
#pragma warning disable CA1303
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = ParseArguments(args);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSec));

string payloadText = await LoadPayloadAsync(options, cts.Token).ConfigureAwait(false);
(string normalizedPayload, string messageId) = NormalizePayload(payloadText);

using var socket = new ClientWebSocket();
await socket.ConnectAsync(new Uri($"ws://{options.Host}:{options.Port}/"), cts.Token).ConfigureAwait(false);

byte[] payloadBytes = Encoding.UTF8.GetBytes(normalizedPayload);
await socket.SendAsync(payloadBytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);

string responseText = await ReceiveResponseAsync(socket, messageId, cts.Token).ConfigureAwait(false);
Console.WriteLine(FormatJson(responseText, options.Pretty));

await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None).ConfigureAwait(false);

static Options ParseArguments(string[] args)
{
    string host = "localhost";
    int port = 49379;
    string? json = null;
    string? jsonFile = null;
    bool pretty = true;
    int timeoutSec = 15;

    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        switch (arg)
        {
            case "--host":
                host = RequireValue(args, ref i, arg);
                break;

            case "--port":
                port = int.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                break;

            case "--json":
                json = RequireValue(args, ref i, arg);
                break;

            case "--json-file":
                jsonFile = RequireValue(args, ref i, arg);
                break;

            case "--timeout-sec":
                timeoutSec = int.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                break;

            case "--pretty":
                pretty = true;
                break;

            case "--compact":
                pretty = false;
                break;

            default:
                throw new ArgumentException($"Unknown argument: {arg}");
        }
    }

    if (string.IsNullOrWhiteSpace(json) == string.IsNullOrWhiteSpace(jsonFile))
    {
        throw new ArgumentException("Specify exactly one of --json or --json-file.");
    }

    return new Options(host, port, json, jsonFile, timeoutSec, pretty);
}

static string RequireValue(string[] args, ref int index, string optionName)
{
    if (index + 1 >= args.Length)
    {
        throw new ArgumentException($"Missing value for {optionName}.");
    }

    index++;
    return args[index];
}

static async Task<string> LoadPayloadAsync(Options options, CancellationToken cancellationToken)
{
    if (!string.IsNullOrWhiteSpace(options.Json))
    {
        return options.Json;
    }

    string path = options.JsonFile!;
    return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
}

static (string Payload, string MessageId) NormalizePayload(string rawJson)
{
    JsonNode? parsed = JsonNode.Parse(rawJson);
    if (parsed is not JsonObject root)
    {
        throw new ArgumentException("Payload must be a JSON object.");
    }

    string? messageId = GetStringProperty(root, "messageId") ?? GetStringProperty(root, "MessageID");
    if (string.IsNullOrWhiteSpace(messageId))
    {
        messageId = Guid.NewGuid().ToString("N");
        root["messageId"] = messageId;
    }

    return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), messageId);
}

static string? GetStringProperty(JsonObject root, string name)
{
    if (!root.TryGetPropertyValue(name, out JsonNode? value) || value is null)
    {
        return null;
    }

    return value.GetValue<string>();
}

static async Task<string> ReceiveResponseAsync(ClientWebSocket socket, string sourceMessageId, CancellationToken cancellationToken)
{
    byte[] buffer = new byte[8192];
    using var stream = new MemoryStream();

    while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
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

        string responseText = Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        if (ResponseMatches(responseText, sourceMessageId))
        {
            return responseText;
        }
    }

    throw new TimeoutException($"Timed out waiting for response to messageId={sourceMessageId}.");
}

static bool ResponseMatches(string responseText, string sourceMessageId)
{
    JsonNode? parsed = JsonNode.Parse(responseText);
    if (parsed is not JsonObject root)
    {
        return false;
    }

    string? candidate = GetStringProperty(root, "sourceMessageId") ?? GetStringProperty(root, "SourceMessageID");
    return string.Equals(candidate, sourceMessageId, StringComparison.Ordinal);
}

static string FormatJson(string json, bool pretty)
{
    JsonNode? parsed = JsonNode.Parse(json);
    return parsed is null
        ? json
        : parsed.ToJsonString(new JsonSerializerOptions { WriteIndented = pretty });
}

internal sealed record Options(
    string Host,
    int Port,
    string? Json,
    string? JsonFile,
    int TimeoutSec,
    bool Pretty);
