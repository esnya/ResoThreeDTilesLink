// Diagnostic console tool; localized resources are unnecessary here.
#pragma warning disable CA1303
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Resonite;

return await RunAsync(args).ConfigureAwait(false);

[SuppressMessage(
    "Reliability",
    "CA1031:DoNotCatchGeneralExceptionTypes",
    Justification = "The benchmark intentionally aggregates send failures without aborting the entire case.")]
static async Task<int> RunAsync(string[] args)
{
    BenchmarkOptions options = BenchmarkOptions.Parse(args);

    ResoniteLink.LinkInterface? linkInterface = null;
    ResoniteSession session;
    try
    {
#pragma warning disable CA2000
        linkInterface = new ResoniteLink.LinkInterface();
#pragma warning restore CA2000
        session = new ResoniteSession(
            linkInterface,
            NullLogger<ResoniteSession>.Instance);
    }
    catch (Exception)
    {
        linkInterface?.Dispose();
        throw;
    }

    await using (session.ConfigureAwait(false))
    {
        await session.ConnectAsync(options.Host, options.Port, CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (!string.IsNullOrWhiteSpace(options.RemoveSlotId))
            {
                await session.RemoveSlotAsync(options.RemoveSlotId, CancellationToken.None).ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"Removed slot {options.RemoveSlotId}").ConfigureAwait(false);
                return 0;
            }

            await Console.Out.WriteLineAsync($"Connected ws://{options.Host}:{options.Port}/").ConfigureAwait(false);
            await Console.Out.WriteLineAsync($"MeshCount={options.MeshCount} Parallelisms={string.Join(",", options.Parallelisms)}").ConfigureAwait(false);
            await Console.Out.WriteLineAsync("Each case creates and removes its own benchmark parent slot.").ConfigureAwait(false);
            await Console.Out.WriteLineAsync().ConfigureAwait(false);

            foreach (int parallelism in options.Parallelisms)
            {
                BenchmarkCaseResult result = await RunCaseAsync(session, parallelism, options.MeshCount).ConfigureAwait(false);
                await Console.Out.WriteLineAsync(
                    $"Parallelism={result.Parallelism} Sent={result.SentCount} Failed={result.FailedCount} " +
                    $"ElapsedMs={result.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}").ConfigureAwait(false);

                if (result.Errors.Count > 0)
                {
                    await Console.Out.WriteLineAsync(
                        $"  FirstError={result.Errors[0].GetType().Name}: {result.Errors[0].Message}").ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    return 0;
}

[SuppressMessage(
    "Reliability",
    "CA1031:DoNotCatchGeneralExceptionTypes",
    Justification = "The benchmark intentionally records individual send failures and continues gathering timings.")]
static async Task<BenchmarkCaseResult> RunCaseAsync(
    ResoniteSession session,
    int parallelism,
    int meshCount)
{
    string parentSlotId = await session
        .CreateSessionChildSlotAsync($"Benchmark p{parallelism} {DateTimeOffset.Now:HH:mm:ss}", CancellationToken.None)
        .ConfigureAwait(false);

    try
    {
        // Warm up parent-local asset slot creation and session-local metadata resolution outside the measurement window.
        _ = await session.StreamPlacedMeshAsync(CreatePayload(parentSlotId, "warmup", 0), CancellationToken.None).ConfigureAwait(false);

        Stopwatch stopwatch = Stopwatch.StartNew();
        var sendTasks = new List<Task<string?>>(meshCount);
        using var gate = new SemaphoreSlim(parallelism, parallelism);

        for (int i = 0; i < meshCount; i++)
        {
            int meshIndex = i;
            sendTasks.Add(SendAsync(meshIndex));
        }

        Exception[]? errors = null;
        int sentCount = 0;

        try
        {
            string?[] slotIds = await Task.WhenAll(sendTasks).ConfigureAwait(false);
            sentCount = slotIds.Count(static id => !string.IsNullOrWhiteSpace(id));
        }
        catch
        {
            errors = sendTasks
                .Where(static task => task.IsFaulted)
                .SelectMany(static task => task.Exception?.InnerExceptions ?? [])
                .ToArray();
            sentCount = sendTasks.Count(static task => task.Status == TaskStatus.RanToCompletion && !string.IsNullOrWhiteSpace(task.Result));
        }

        stopwatch.Stop();
        errors ??= [];

        return new BenchmarkCaseResult(parallelism, sentCount, errors.Length, stopwatch.Elapsed, errors);

        async Task<string?> SendAsync(int meshIndex)
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                return await session
                    .StreamPlacedMeshAsync(CreatePayload(parentSlotId, $"mesh_{meshIndex:D2}", meshIndex + 1), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _ = gate.Release();
            }
        }
    }
    finally
    {
        await session.RemoveSlotAsync(parentSlotId, CancellationToken.None).ConfigureAwait(false);
    }
}

static PlacedMeshPayload CreatePayload(string parentSlotId, string nameSuffix, int offsetIndex)
{
    float x = offsetIndex * 0.05f;

    return new PlacedMeshPayload(
        $"bench_{nameSuffix}",
        [
            new Vector3(x, 0f, 0f),
            new Vector3(x, 0.25f, 0f),
            new Vector3(x + 0.25f, 0f, 0f)
        ],
        [0, 1, 2],
        [],
        false,
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.One,
        null,
        null,
        parentSlotId);
}

internal sealed record BenchmarkCaseResult(
    int Parallelism,
    int SentCount,
    int FailedCount,
    TimeSpan Elapsed,
    IReadOnlyList<Exception> Errors);

internal sealed record BenchmarkOptions(
    string Host,
    int Port,
    int MeshCount,
    int[] Parallelisms,
    string? RemoveSlotId)
{
    public static BenchmarkOptions Parse(IReadOnlyList<string> args)
    {
        string host = "localhost";
        int port = 49379;
        int meshCount = 8;
        int[] parallelisms = [1, 2, 4, 8];
        string? removeSlotId = null;

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
                case "--mesh-count":
                    meshCount = int.Parse(ReadRequiredValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--parallelism":
                    parallelisms = ReadRequiredValue(args, ref i, arg)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static value => int.Parse(value, CultureInfo.InvariantCulture))
                        .ToArray();
                    break;
                case "--remove-slot-id":
                    removeSlotId = ReadRequiredValue(args, ref i, arg);
                    break;
                case "--help":
                case "-h":
                    PrintHelpAndExit(0);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument: {arg}");
            }
        }

        if (port <= 0)
        {
            throw new InvalidOperationException($"Port must be positive: {port}");
        }

        if (meshCount <= 0)
        {
            throw new InvalidOperationException($"Mesh count must be positive: {meshCount}");
        }

        if (parallelisms.Length == 0 || parallelisms.Any(static value => value <= 0))
        {
            throw new InvalidOperationException("Parallelism values must all be positive.");
        }

        return new BenchmarkOptions(host, port, meshCount, parallelisms, removeSlotId);
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
        Console.WriteLine("Usage: dotnet run --project tools/ResoniteSendBenchmark -- [options]");
        Console.WriteLine("  --host <host>             Resonite Link host. Default: localhost");
        Console.WriteLine("  --port <port>             Resonite Link port. Default: 49379");
        Console.WriteLine("  --mesh-count <count>      Mesh sends per case. Default: 8");
        Console.WriteLine("  --parallelism <csv>       Parallelism cases. Default: 1,2,4,8");
        Console.WriteLine("  --remove-slot-id <slot>   Remove a slot and exit.");
        Environment.Exit(exitCode);
    }
}
