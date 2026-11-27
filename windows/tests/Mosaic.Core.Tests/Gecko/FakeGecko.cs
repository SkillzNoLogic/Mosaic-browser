using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Tests.Gecko;

public enum FakeTransport { Tcp, Unix }

/// <summary>
/// A stand-in for Gecko's debugging server: speaks just enough of the remote debugging protocol
/// to exercise <see cref="RdpClient"/> and, through <see cref="Responder"/>, <see cref="GeckoEngine"/>.
/// Port of <c>FakeGecko</c> in <c>Tests/MosaicCoreTests/RDPClientTests.swift</c>.
/// </summary>
internal sealed class FakeGecko : IDisposable
{
    public static readonly string LongText = string.Concat(Enumerable.Repeat("mosaic ", 3000));

    /// <summary>Longer than the client's read buffer, so it arrives in pieces and makes the buffer grow.</summary>
    public static readonly string HugeText = string.Concat(Enumerable.Repeat("0123456789abcdef", 20_000));

    /// <summary>Thrown by a <see cref="Responder"/> to make the evaluation fail like a script that threw.</summary>
    public sealed class ScriptError(string message) : Exception(message);

    /// <summary>Send the root greeting only after the first request has arrived, the ordering
    /// that once made the client mistake the greeting for a reply.</summary>
    public bool GreetLate { get; init; }

    /// <summary>Close the connection instead of answering an evaluation containing this text.</summary>
    public string? HangUpOn { get; init; }

    /// <summary>Write every packet one byte at a time, so no read ever holds a whole packet.</summary>
    public bool Dribble { get; init; }

    /// <summary>Answers evaluations the built-in cases do not know; null falls back to an echo.</summary>
    public Func<string, JsonNode?>? Responder { get; init; }

    private readonly FakeTransport _transport;
    private readonly Socket _listener;
    private readonly string? _socketPath;
    private Socket? _connection;
    private int _counter;

    public FakeGecko(FakeTransport transport = FakeTransport.Tcp, int port = 0)
    {
        _transport = transport;
        if (transport == FakeTransport.Unix)
        {
            // Short on purpose: a socket path may be at most about a hundred bytes.
            _socketPath = Path.Combine(Path.GetTempPath(), $"mrdp-{Guid.NewGuid().ToString("N")[..8]}.sock");
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        }
        else
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
        _listener.Listen(1);
        _ = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndPoint!).Port;

    public Task<RdpClient> ConnectAsync() => _transport == FakeTransport.Unix
        ? RdpClient.ConnectUnixAsync(_socketPath!)
        : RdpClient.ConnectTcpAsync("127.0.0.1", Port);

    public void Dispose()
    {
        try { _connection?.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        _connection?.Dispose();
        _listener.Dispose();
        if (_socketPath is not null) File.Delete(_socketPath);
    }

    private async Task ServeAsync()
    {
        try
        {
            _connection = await _listener.AcceptAsync();
            var greeting = new JsonObject { ["from"] = "root", ["applicationType"] = "browser", ["traits"] = new JsonObject() };
            var greeted = false;
            if (!GreetLate)
            {
                await SendAsync(greeting);
                greeted = true;
            }

            var buffer = new List<byte>();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await _connection.ReceiveAsync(chunk);
                if (count <= 0) break;
                buffer.AddRange(chunk.AsSpan(0, count));
                while (buffer.IndexOf((byte)':') is > 0 and var colon &&
                       int.Parse(Encoding.ASCII.GetString(buffer.GetRange(0, colon).ToArray())) is var length &&
                       buffer.Count >= colon + 1 + length)
                {
                    var body = buffer.GetRange(colon + 1, length).ToArray();
                    buffer.RemoveRange(0, colon + 1 + length);
                    if (JsonNode.Parse(body) is not JsonObject packet) continue;
                    if (!greeted)
                    {
                        await SendAsync(greeting);
                        greeted = true;
                    }
                    if (!await HandleAsync(packet)) return;
                }
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or IOException)
        {
            // The test is over.
        }
    }

    /// <summary>Returns false when the connection was deliberately dropped.</summary>
    private async Task<bool> HandleAsync(JsonObject packet)
    {
        var to = (string?)packet["to"] ?? "";
        var type = (string?)packet["type"] ?? "";
        switch (to, type)
        {
            case ("root", "getProcess"):
                await SendAsync(new JsonObject
                {
                    ["from"] = "root",
                    ["processDescriptor"] = new JsonObject { ["actor"] = "process1", ["id"] = 0, ["isParent"] = true },
                });
                break;
            case ("process1", "getTarget"):
                await SendAsync(new JsonObject
                {
                    ["from"] = "process1",
                    ["process"] = new JsonObject { ["actor"] = "target1", ["consoleActor"] = "console1" },
                });
                break;
            case ("console1", "evaluateJSAsync"):
                var text = (string?)packet["text"] ?? "";
                if (HangUpOn is not null && text.Contains(HangUpOn))
                {
                    _connection!.Shutdown(SocketShutdown.Both);
                    return false;
                }
                var id = $"result{++_counter}";
                await SendAsync(new JsonObject { ["from"] = "console1", ["resultID"] = id });
                var result = new JsonObject { ["from"] = "console1", ["type"] = "evaluationResult", ["resultID"] = id };
                switch (text)
                {
                    case "1+1": result["result"] = 2; break;
                    case "str": result["result"] = "hello"; break;
                    case "flag": result["result"] = true; break;
                    case "undef": result["result"] = new JsonObject { ["type"] = "undefined" }; break;
                    case "null": result["result"] = new JsonObject { ["type"] = "null" }; break;
                    case "object": result["result"] = new JsonObject { ["type"] = "object", ["class"] = "Object", ["actor"] = "obj1" }; break;
                    case "huge": result["result"] = HugeText; break;
                    case "long":
                        result["result"] = new JsonObject
                        {
                            ["type"] = "longString", ["actor"] = "long1", ["length"] = LongText.Length,
                            ["initial"] = LongText[..1000],
                        };
                        break;
                    case "throw":
                        Throw(result, "Error: boom");
                        break;
                    case "reject":
                        // What Gecko sends when an awaited promise rejects: a flag and nothing else.
                        result["hasException"] = false;
                        result["topLevelAwaitRejected"] = true;
                        break;
                    case "noise":
                        // Unrelated events from the same actor must be ignored.
                        await SendAsync(new JsonObject
                        {
                            ["from"] = "console1", ["type"] = "consoleAPICall", ["message"] = new JsonObject { ["level"] = "log" },
                        });
                        result["result"] = "after noise";
                        break;
                    default:
                        try
                        {
                            result["result"] = Responder?.Invoke(text) ?? $"echo:{text}";
                        }
                        catch (ScriptError error)
                        {
                            Throw(result, error.Message);
                        }
                        break;
                }
                await SendAsync(result);
                break;
            case ("long1", "substring"):
                var start = (int?)packet["start"] ?? 0;
                var end = Math.Min((int?)packet["end"] ?? 0, LongText.Length);
                await SendAsync(new JsonObject { ["from"] = "long1", ["substring"] = LongText[start..end] });
                break;
            default:
                await SendAsync(new JsonObject
                {
                    ["from"] = to, ["error"] = "noSuchActor", ["message"] = $"No such actor for ID: {to}",
                });
                break;
        }
        return true;

        static void Throw(JsonObject result, string message)
        {
            result["hasException"] = true;
            result["exceptionMessage"] = message;
            result["result"] = new JsonObject { ["type"] = "undefined" };
        }
    }

    private async Task SendAsync(JsonObject packet)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(packet);
        byte[] frame = [.. Encoding.ASCII.GetBytes($"{body.Length}:"), .. body];
        if (Dribble)
        {
            for (var index = 0; index < frame.Length; index++) await _connection!.SendAsync(frame.AsMemory(index, 1));
        }
        else
        {
            for (var sent = 0; sent < frame.Length;) sent += await _connection!.SendAsync(frame.AsMemory(sent));
        }
    }
}
