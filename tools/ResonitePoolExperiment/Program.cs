// Experimental console tool; localized resources are unnecessary here.
#pragma warning disable CA1303
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Resonite;

#pragma warning disable CA2007
return await RunAsync(args).ConfigureAwait(false);
#pragma warning restore CA2007

[SuppressMessage(
    "Reliability",
    "CA1031:DoNotCatchGeneralExceptionTypes",
    Justification = "The experiment aggregates send failures to compare strategies.")]
static async Task<int> RunAsync(string[] args)
{
    ExperimentOptions options = ExperimentOptions.Parse(args);

    SessionPool pool = await SessionPool.ConnectAsync(options.Host, options.Port, options.PoolSize).ConfigureAwait(false);
    await using (pool.ConfigureAwait(false))
    {
        TextureScenario texture = CreateTextureScenario(options.TextureSize, options.TextureSeed);

        await Console.Out.WriteLineAsync($"Connected ws://{options.Host}:{options.Port}/").ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            $"MeshCount={options.MeshCount} Parallelisms={string.Join(",", options.Parallelisms)} PoolSize={options.PoolSize}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            $"Texture={texture.Width}x{texture.Height} PNGBytes={texture.BaseBytes.Length.ToString("N0", CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            "Single uses one long-lived session; pooled distributes sends across long-lived sessions; short-lived reconnects for every send.").ConfigureAwait(false);
        await Console.Out.WriteLineAsync().ConfigureAwait(false);

        foreach (int parallelism in options.Parallelisms)
        {
            foreach (ExperimentMode mode in new[] { ExperimentMode.Single, ExperimentMode.Pooled, ExperimentMode.ShortLived })
            {
                ExperimentCaseResult result = await RunCaseAsync(
                    pool,
                    mode,
                    parallelism,
                    options.MeshCount,
                    texture,
                    CancellationToken.None).ConfigureAwait(false);
                await WriteCaseResultAsync(result).ConfigureAwait(false);
            }

            await Console.Out.WriteLineAsync().ConfigureAwait(false);
        }
    }

    return 0;
}

[SuppressMessage(
    "Reliability",
    "CA1031:DoNotCatchGeneralExceptionTypes",
    Justification = "The experiment records per-send failures and continues gathering timings.")]
static async Task<ExperimentCaseResult> RunCaseAsync(
    SessionPool pool,
    ExperimentMode mode,
    int parallelism,
    int meshCount,
    TextureScenario texture,
    CancellationToken cancellationToken)
{
    string parentSlotId = await pool.Controller
        .CreateSessionChildSlotAsync($"{mode} p{parallelism} {DateTimeOffset.Now:HH:mm:ss}", cancellationToken)
        .ConfigureAwait(false);

    try
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        var sendTasks = new List<Task<SendResult>>(meshCount);
        using var gate = new SemaphoreSlim(parallelism, parallelism);
        int pooledWorkerIndex = -1;

        for (int i = 0; i < meshCount; i++)
        {
            int meshIndex = i;
            sendTasks.Add(SendAsync(meshIndex));
        }

        SendResult[] results = await Task.WhenAll(sendTasks).ConfigureAwait(false);
        stopwatch.Stop();

        string[] nonEmptySlotIds = results
            .Select(static result => result.SlotId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();

        int sentCount = nonEmptySlotIds.Length;
        int failedCount = results.Length - sentCount;
        int duplicateSlotIds = nonEmptySlotIds.Length - nonEmptySlotIds.Distinct(StringComparer.Ordinal).Count();
        double meshesPerSecond = stopwatch.Elapsed.TotalSeconds <= 0d
            ? sentCount
            : sentCount / stopwatch.Elapsed.TotalSeconds;

        return new ExperimentCaseResult(
            mode,
            parallelism,
            sentCount,
            failedCount,
            duplicateSlotIds,
            stopwatch.Elapsed,
            meshesPerSecond,
            results.Where(static result => result.Error is not null).Select(static result => result.Error!).ToArray());

        async Task<SendResult> SendAsync(int meshIndex)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return mode switch
                {
                    ExperimentMode.Single => await SendWithSessionAsync(
                        pool.GetSingleWorker().Session,
                        parentSlotId,
                        meshIndex,
                        texture,
                        cancellationToken).ConfigureAwait(false),
                    ExperimentMode.Pooled => await SendWithSessionAsync(
                        pool.GetWorker(Interlocked.Increment(ref pooledWorkerIndex)).Session,
                        parentSlotId,
                        meshIndex,
                        texture,
                        cancellationToken).ConfigureAwait(false),
                    ExperimentMode.ShortLived => await SendWithShortLivedSessionAsync(
                        pool.Host,
                        pool.Port,
                        parentSlotId,
                        meshIndex,
                        texture,
                        cancellationToken).ConfigureAwait(false),
                    _ => throw new InvalidOperationException($"Unsupported mode: {mode}")
                };
            }
            catch (Exception ex)
            {
                return new SendResult(null, ex);
            }
            finally
            {
                _ = gate.Release();
            }
        }
    }
    finally
    {
        await pool.Controller.RemoveSlotAsync(parentSlotId, cancellationToken).ConfigureAwait(false);
    }
}

static async Task<SendResult> SendWithSessionAsync(
    ResoniteSession session,
    string parentSlotId,
    int meshIndex,
    TextureScenario texture,
    CancellationToken cancellationToken)
{
    string? slotId = await session
        .StreamPlacedMeshAsync(
            CreatePayload(parentSlotId, meshIndex, texture.CreateBytes(meshIndex)),
            cancellationToken)
        .ConfigureAwait(false);
    return new SendResult(slotId, null);
}

static async Task<SendResult> SendWithShortLivedSessionAsync(
    string host,
    int port,
    string parentSlotId,
    int meshIndex,
    TextureScenario texture,
    CancellationToken cancellationToken)
{
    ResoniteSession session = SessionPool.CreateSession();
    await using (session.ConfigureAwait(false))
    {
        await session.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        string? slotId = await session
            .StreamPlacedMeshAsync(
                CreatePayload(parentSlotId, meshIndex, texture.CreateBytes(meshIndex)),
                cancellationToken)
            .ConfigureAwait(false);
        return new SendResult(slotId, null);
    }
}

static async Task WriteCaseResultAsync(ExperimentCaseResult result)
{
    await Console.Out.WriteLineAsync(
        $"{result.Mode,-10} Parallelism={result.Parallelism} Sent={result.SentCount} Failed={result.FailedCount} " +
        $"DupSlotIds={result.DuplicateSlotIds} ElapsedMs={result.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} " +
        $"MeshesPerSec={result.MeshesPerSecond.ToString("F2", CultureInfo.InvariantCulture)}").ConfigureAwait(false);

    if (result.Errors.Count > 0)
    {
        await Console.Out.WriteLineAsync(
            $"  FirstError={result.Errors[0].GetType().Name}: {result.Errors[0].Message}").ConfigureAwait(false);
    }
}

static PlacedMeshPayload CreatePayload(string parentSlotId, int meshIndex, byte[] textureBytes)
{
    float x = meshIndex * 0.15f;

    return new PlacedMeshPayload(
        $"texture_mesh_{meshIndex:D2}",
        [
            new Vector3(x, 0f, 0f),
            new Vector3(x, 1f, 0f),
            new Vector3(x + 1f, 0f, 0f),
            new Vector3(x + 1f, 1f, 0f)
        ],
        [0, 1, 2, 2, 1, 3],
        [
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f)
        ],
        true,
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.One,
        textureBytes,
        ".png",
        parentSlotId);
}

static TextureScenario CreateTextureScenario(int size, int seed)
{
    byte[] baseBytes = PngNoiseWriter.Create(size, size, seed);
    return new TextureScenario(size, size, baseBytes, seed);
}

internal enum ExperimentMode
{
    Single,
    Pooled,
    ShortLived
}

internal sealed record ExperimentCaseResult(
    ExperimentMode Mode,
    int Parallelism,
    int SentCount,
    int FailedCount,
    int DuplicateSlotIds,
    TimeSpan Elapsed,
    double MeshesPerSecond,
    IReadOnlyList<Exception> Errors);

internal sealed record SendResult(string? SlotId, Exception? Error);

internal sealed record TextureScenario(
    int Width,
    int Height,
    byte[] BaseBytes,
    int Seed)
{
    public byte[] CreateBytes(int meshIndex)
    {
        if (meshIndex == 0)
        {
            return BaseBytes;
        }

        return PngNoiseWriter.Create(Width, Height, unchecked(Seed + meshIndex));
    }
}

internal sealed record ExperimentOptions(
    string Host,
    int Port,
    int MeshCount,
    int PoolSize,
    int TextureSize,
    int TextureSeed,
    int[] Parallelisms)
{
    public static ExperimentOptions Parse(IReadOnlyList<string> args)
    {
        string host = "localhost";
        int port = 49379;
        int meshCount = 8;
        int poolSize = 4;
        int textureSize = 1024;
        int textureSeed = 12345;
        int[] parallelisms = [4];

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
                case "--pool-size":
                    poolSize = int.Parse(ReadRequiredValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--texture-size":
                    textureSize = int.Parse(ReadRequiredValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--texture-seed":
                    textureSeed = int.Parse(ReadRequiredValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--parallelism":
                    parallelisms = ReadRequiredValue(args, ref i, arg)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static value => int.Parse(value, CultureInfo.InvariantCulture))
                        .ToArray();
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

        if (poolSize <= 0)
        {
            throw new InvalidOperationException($"Pool size must be positive: {poolSize}");
        }

        if (textureSize < 32)
        {
            throw new InvalidOperationException($"Texture size must be at least 32: {textureSize}");
        }

        if (parallelisms.Length == 0 || parallelisms.Any(static value => value <= 0))
        {
            throw new InvalidOperationException("Parallelism values must all be positive.");
        }

        return new ExperimentOptions(host, port, meshCount, poolSize, textureSize, textureSeed, parallelisms);
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
        Console.WriteLine("Usage: dotnet run --project tools/ResonitePoolExperiment -- [options]");
        Console.WriteLine("  --host <host>             Resonite Link host. Default: localhost");
        Console.WriteLine("  --port <port>             Resonite Link port. Default: 49379");
        Console.WriteLine("  --mesh-count <count>      Mesh sends per case. Default: 8");
        Console.WriteLine("  --pool-size <count>       Worker session count for pooled mode. Default: 4");
        Console.WriteLine("  --texture-size <px>       Square texture size. Default: 1024");
        Console.WriteLine("  --texture-seed <int>      Base noise seed. Default: 12345");
        Console.WriteLine("  --parallelism <csv>       Parallelism cases. Default: 4");
        Environment.Exit(exitCode);
    }
}

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
        var link = new ResoniteLink.LinkInterface();
#pragma warning restore CA2000
        return new ResoniteSession(link, NullLogger<ResoniteSession>.Instance);
    }

    internal sealed record WorkerLease(ResoniteSession Session);
}

internal static class PngNoiseWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Create(int width, int height, int seed)
    {
        byte[] imageData = CreateImageData(width, height, seed);

        using var output = new MemoryStream();
        output.Write(Signature);

        WriteChunk(output, "IHDR", BuildIhdr(width, height));
        WriteChunk(output, "IDAT", Compress(imageData));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static byte[] CreateImageData(int width, int height, int seed)
    {
        int stride = (width * 4) + 1;
        byte[] data = new byte[stride * height];
        var random = new XorShift32(unchecked((uint)seed) ^ 0xA511E9B3u);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            data[rowStart] = 0;
            for (int x = 0; x < width; x++)
            {
                int pixelOffset = rowStart + 1 + (x * 4);
                byte r = random.NextByte();
                byte g = random.NextByte();
                byte b = random.NextByte();
                byte a = 255;

                if (((x / 64) + (y / 64)) % 2 == 0)
                {
                    r = (byte)(255 - r);
                    b = (byte)(255 - b);
                }

                data[pixelOffset] = r;
                data[pixelOffset + 1] = g;
                data[pixelOffset + 2] = b;
                data[pixelOffset + 3] = a;
            }
        }

        return data;
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        byte[] ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        return ihdr;
    }

    private static byte[] Compress(byte[] rawData)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(rawData, 0, rawData.Length);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string chunkType, byte[] chunkData)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(chunkType);
        Span<byte> lengthBuffer = stackalloc byte[4];
        WriteUInt32BigEndian(lengthBuffer, (uint)chunkData.Length);
        output.Write(lengthBuffer);
        output.Write(typeBytes, 0, typeBytes.Length);
        output.Write(chunkData, 0, chunkData.Length);

        uint crc = ComputeCrc(typeBytes, chunkData);
        Span<byte> crcBuffer = stackalloc byte[4];
        WriteUInt32BigEndian(crcBuffer, crc);
        output.Write(crcBuffer);
    }

    private static uint ComputeCrc(byte[] typeBytes, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc(uint crc, byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static void WriteUInt32BigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private struct XorShift32(uint seed)
    {
        private uint _state = seed == 0 ? 0x6D2B79F5u : seed;

        public byte NextByte()
        {
            uint value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return (byte)(value & 0xFF);
        }
    }
}
