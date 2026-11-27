using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Gecko;

public enum RdpErrorKind { ConnectFailed, Closed, Remote, Protocol }

public sealed class RdpException(RdpErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public RdpErrorKind Kind { get; } = kind;

    internal static RdpException ConnectFailed(string why, Exception? inner = null) =>
        new(RdpErrorKind.ConnectFailed, $"Could not connect to Gecko: {why}", inner);

    internal static RdpException ConnectionClosed() =>
        new(RdpErrorKind.Closed, "The connection to Gecko was closed.");

    internal static RdpException Remote(string why) =>
        new(RdpErrorKind.Remote, $"Gecko reported an error: {why}");

    internal static RdpException Protocol(string why) =>
        new(RdpErrorKind.Protocol, $"Unexpected reply from Gecko: {why}");
}

/// <summary>
/// Minimal client for the Firefox Remote Debugging Protocol. Mosaic uses one capability of it:
/// evaluating privileged JavaScript in Gecko's parent process, which is how the Mosaic agent
/// (<c>agent.sys.mjs</c>) is loaded and driven. Packets are <c>&lt;byte length&gt;:&lt;json&gt;</c>.
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Gecko/RDPClient.swift</c> (behaviour pinned down by
/// <c>RDPClientTests.swift</c>). On Windows Gecko's debugging server listens on a loopback TCP
/// port (Unix-socket paths are not supported there); the Unix-socket transport exists so the
/// client can also be exercised on macOS/Linux. All members are thread-safe.
/// </remarks>
public sealed class RdpClient : IDisposable
{
    /// <summary>
    /// Upper bound on one packet. Nothing Mosaic asks for comes near it; it exists so a corrupt
    /// length prefix is reported as a broken connection instead of an attempt to buffer gigabytes.
    /// </summary>
    private const int MaxPacketBytes = 1 << 30;

    private enum PendingKind
    {
        /// <summary>Completed with the actor's direct reply.</summary>
        Reply,

        /// <summary>The direct reply only carries a <c>resultID</c>; the value arrives later in
        /// an <c>evaluationResult</c> event.</summary>
        Evaluation,
    }

    private readonly record struct Pending(PendingKind Kind, TaskCompletionSource<JsonObject> Waiter);

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Serialises writers. Held across "take a place in the actor's queue" and "write
    /// the request", because those two must happen in the same order for every request.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<Pending>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _evaluations = new(StringComparer.Ordinal);
    private string? _consoleActor;
    private bool _closed;

    private RdpClient(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Connects once; throws <see cref="RdpException"/> (ConnectFailed) if nothing is listening yet.</summary>
    public static Task<RdpClient> ConnectTcpAsync(string host, int port, CancellationToken cancellation = default) =>
        ConnectAsync(
            // Requests are small and each one waits for its reply, so Nagle's algorithm would only add latency.
            () => new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true },
            socket => socket.ConnectAsync(host, port, cancellation));

    public static Task<RdpClient> ConnectUnixAsync(string socketPath, CancellationToken cancellation = default) =>
        ConnectAsync(
            () => new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
            socket => socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellation));

    private static async Task<RdpClient> ConnectAsync(Func<Socket> create, Func<Socket, ValueTask> connect)
    {
        Socket? socket = null;
        try
        {
            socket = create();
            await connect(socket).ConfigureAwait(false);
            return new RdpClient(socket);
        }
        catch (Exception error) when (error is SocketException or ArgumentException or NotSupportedException)
        {
            // ArgumentException: a Unix socket path longer than the platform allows.
            socket?.Dispose();
            throw RdpException.ConnectFailed(error.Message, error);
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    /// <summary>Raised once, on a thread-pool thread, when the connection goes away for any reason.</summary>
    public event Action? Closed;

    /// <summary>
    /// True once the connection is gone or <see cref="Close"/> was called. Lets a caller that
    /// subscribed to <see cref="Closed"/> late find out it has already missed the event.
    /// </summary>
    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    /// <summary>Finds the parent-process console, the actor that evaluates privileged script.</summary>
    public async Task AttachToParentProcessAsync()
    {
        var process = await SendAsync(
            new JsonObject { ["to"] = "root", ["type"] = "getProcess", ["id"] = 0 }, PendingKind.Reply).ConfigureAwait(false);
        if (Text((process["processDescriptor"] as JsonObject)?["actor"]) is not { } descriptor)
            throw RdpException.Protocol("no parent process descriptor");

        var target = await SendAsync(
            new JsonObject { ["to"] = descriptor, ["type"] = "getTarget" }, PendingKind.Reply).ConfigureAwait(false);
        if (Text((target["process"] as JsonObject)?["consoleActor"]) is not { } console)
            throw RdpException.Protocol("no console actor for the parent process");

        lock (_gate) _consoleActor = console;
    }

    /// <summary>
    /// Evaluates <paramref name="script"/> with system privileges in Gecko's parent process and
    /// returns its value. Promises are awaited. Strings, numbers and booleans come back as
    /// <see cref="JsonValue"/>; <c>undefined</c>/<c>null</c> as null; long strings are fetched in
    /// full; any other object as its grip (<see cref="JsonObject"/>).
    /// </summary>
    /// <exception cref="RdpException">Remote: the script threw. Closed: the connection is gone.
    /// Protocol: not attached, or an unexpected reply.</exception>
    public async Task<JsonNode?> EvaluateAsync(string script)
    {
        string? console;
        lock (_gate) console = _consoleActor;
        if (console is null) throw RdpException.Protocol("not attached to the parent process");

        // `mapped.await` makes the console await a returned promise, so asynchronous agent
        // methods deliver their final value instead of a promise grip.
        var packet = await SendAsync(
            new JsonObject
            {
                ["to"] = console,
                ["type"] = "evaluateJSAsync",
                ["text"] = script,
                ["mapped"] = new JsonObject { ["await"] = true },
            },
            PendingKind.Evaluation).ConfigureAwait(false);

        if (packet["hasException"] is JsonValue thrown && thrown.TryGetValue(out bool hasException) && hasException)
            throw RdpException.Remote(Describe(packet["exceptionMessage"]) ?? "script threw an exception");

        // An awaited promise that rejects is not reported as an exception: the reply carries only
        // this flag, with no message and no value. Without this check it would read as "undefined".
        if (packet["topLevelAwaitRejected"] is JsonValue rejected && rejected.TryGetValue(out bool wasRejected) && wasRejected)
            throw RdpException.Remote("an awaited promise was rejected");

        var result = packet["result"];
        packet.Remove("result"); // hand the value over without its parent packet attached
        return await UnwrapAsync(result).ConfigureAwait(false);
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }
        // Ends the read loop, which then fails everything still waiting and raises Closed.
        // Cancelling covers a read that is parked in the kernel; disposing covers a write.
        _closing.Cancel();
        _socket.Dispose();
    }

    public void Dispose() => Close();

    // ---- Requests -------------------------------------------------------------------------

    private async Task<JsonNode?> UnwrapAsync(JsonNode? value)
    {
        if (value is not JsonObject grip || Text(grip["type"]) is not { } type) return value;
        switch (type)
        {
            case "undefined" or "null":
                return null;
            case "longString":
                // Strings over 10 000 characters arrive as a handle to fetch in full.
                if (Text(grip["actor"]) is not { } actor ||
                    grip["length"] is not JsonValue size || !size.TryGetValue(out int length))
                {
                    return Detach(grip, "initial");
                }
                var reply = await SendAsync(
                    new JsonObject { ["to"] = actor, ["type"] = "substring", ["start"] = 0, ["end"] = length },
                    PendingKind.Reply).ConfigureAwait(false);
                return Detach(reply, "substring") ?? Detach(grip, "initial");
            default:
                return grip;
        }
    }

    private static JsonNode? Detach(JsonObject owner, string name)
    {
        var value = owner[name];
        owner.Remove(name);
        return value;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string? Describe(JsonNode? value) => value switch
    {
        JsonObject grip => Text(grip["initial"]) ?? grip.ToJsonString(),
        _ => Text(value),
    };

    private async Task<JsonObject> SendAsync(JsonObject packet, PendingKind kind)
    {
        var actor = Text(packet["to"]) ?? throw RdpException.Protocol("packet has no target");
        var body = JsonSerializer.SerializeToUtf8Bytes(packet);
        var header = Encoding.ASCII.GetBytes($"{body.Length}:");
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);

        // Completing a waiter must never run the caller's continuation on the read loop.
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _writeLock.WaitAsync(_closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw RdpException.ConnectionClosed();
        }

        try
        {
            // Replies carry no request ID; each actor answers in order, so the queue position
            // and the write must be decided together, which holding the write lock guarantees.
            lock (_gate)
            {
                if (_closed) throw RdpException.ConnectionClosed();
                if (!_pending.TryGetValue(actor, out var queue)) _pending[actor] = queue = new Queue<Pending>();
                queue.Enqueue(new Pending(kind, waiter));
            }
            await _stream.WriteAsync(frame, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The read loop fails every waiter, this one included, once the socket is gone.
            Close();
        }
        finally
        {
            _writeLock.Release();
        }

        return await waiter.Task.ConfigureAwait(false);
    }

    // ---- Reading --------------------------------------------------------------------------

    private async Task ReadLoopAsync()
    {
        var frames = new FrameBuffer();
        try
        {
            while (true)
            {
                var count = await _stream.ReadAsync(frames.Free(), _closing.Token).ConfigureAwait(false);
                if (count <= 0) break;
                frames.Advance(count);
                while (frames.TryTake(out var body))
                {
                    if (TryParse(body) is { } packet) Dispatch(packet);
                }
            }
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException
            or OperationCanceledException or InvalidDataException)
        {
            // Any of these means the conversation is over; what matters is reported below.
        }
        finally
        {
            FailAll();
        }
    }

    private static JsonObject? TryParse(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null; // not something a waiter could be expecting; skip it
        }
    }

    private void Dispatch(JsonObject packet)
    {
        if (Text(packet["from"]) is not { } from) return;

        // The root actor greets every new connection. It is not a reply, and must not be
        // mistaken for the answer to a request sent before it arrived.
        if (from == "root" && packet.ContainsKey("applicationType")) return;

        var failure = Text(packet["error"]);

        // Unsolicited events are the packets that carry a `type` and no error.
        if (failure is null && Text(packet["type"]) is { } type)
        {
            if (type != "evaluationResult" || Text(packet["resultID"]) is not { } id) return;
            TaskCompletionSource<JsonObject>? evaluation;
            lock (_gate) _evaluations.Remove(id, out evaluation);
            evaluation?.TrySetResult(packet);
            return;
        }

        Pending next;
        lock (_gate)
        {
            if (!_pending.TryGetValue(from, out var queue) || queue.Count == 0) return;
            next = queue.Dequeue();

            if (failure is null && next.Kind == PendingKind.Evaluation && Text(packet["resultID"]) is { } id)
            {
                // Registered before the lock is released: the event with the value may be the
                // very next packet.
                _evaluations[id] = next.Waiter;
                return;
            }
        }

        if (failure is not null)
        {
            var message = Text(packet["message"]) is { } detail ? $"{failure}: {detail}" : failure;
            next.Waiter.TrySetException(RdpException.Remote(message));
        }
        else if (next.Kind == PendingKind.Evaluation)
        {
            next.Waiter.TrySetException(RdpException.Protocol("evaluation reply had no resultID"));
        }
        else
        {
            next.Waiter.TrySetResult(packet);
        }
    }

    private void FailAll()
    {
        List<TaskCompletionSource<JsonObject>> waiting;
        lock (_gate)
        {
            _closed = true;
            waiting = [.. _pending.Values.SelectMany(queue => queue).Select(entry => entry.Waiter), .. _evaluations.Values];
            _pending.Clear();
            _evaluations.Clear();
        }

        foreach (var waiter in waiting) waiter.TrySetException(RdpException.ConnectionClosed());

        _stream.Dispose();
        _socket.Dispose();
        Interlocked.Exchange(ref Closed, null)?.Invoke();
    }

    /// <summary>Accumulates bytes from the socket and hands out complete packet bodies.</summary>
    private sealed class FrameBuffer
    {
        private const int ReadChunk = 64 * 1024;

        /// <summary>A length prefix is a handful of decimal digits; anything longer is not one.</summary>
        private const int MaxHeaderBytes = 10;

        private byte[] _bytes = new byte[ReadChunk];
        private int _start;
        private int _end;

        /// <summary>Space for the next read, after compacting or growing as needed.</summary>
        public Memory<byte> Free()
        {
            if (_bytes.Length - _end < ReadChunk)
            {
                var used = _end - _start;
                var target = used + ReadChunk <= _bytes.Length
                    ? _bytes
                    : new byte[Math.Max(_bytes.Length * 2, used + ReadChunk)];
                Buffer.BlockCopy(_bytes, _start, target, 0, used);
                _bytes = target;
                _start = 0;
                _end = used;
            }
            return _bytes.AsMemory(_end);
        }

        public void Advance(int count) => _end += count;

        /// <summary>
        /// Takes the next complete packet body, valid until the next call to <see cref="Free"/>.
        /// </summary>
        /// <exception cref="InvalidDataException">The stream is not framed as expected. That
        /// includes bulk packets (<c>bulk actor type length:</c>), which are never requested.</exception>
        public bool TryTake(out ReadOnlySpan<byte> body)
        {
            body = default;
            var unread = _bytes.AsSpan(_start, _end - _start);
            var colon = unread.IndexOf((byte)':');
            if (colon < 0)
            {
                if (unread.Length > MaxHeaderBytes) throw new InvalidDataException("no length prefix");
                return false;
            }
            if (colon is 0 or > MaxHeaderBytes) throw new InvalidDataException("malformed length prefix");

            var length = 0L;
            foreach (var digit in unread[..colon])
            {
                if (digit is < (byte)'0' or > (byte)'9') throw new InvalidDataException("malformed length prefix");
                length = length * 10 + (digit - '0');
            }
            if (length > MaxPacketBytes) throw new InvalidDataException("packet too large");

            var total = colon + 1 + (int)length;
            if (unread.Length < total) return false;

            body = unread.Slice(colon + 1, (int)length);
            _start += total;
            return true;
        }
    }
}
