using System.Text.Json.Nodes;

namespace Mosaic.Core.Gecko;

public enum RdpErrorKind { ConnectFailed, Closed, Remote, Protocol }

public sealed class RdpException(RdpErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public RdpErrorKind Kind { get; } = kind;
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
    private RdpClient() { }

    /// <summary>Connects once; throws <see cref="RdpException"/> (ConnectFailed) if nothing is listening yet.</summary>
    public static Task<RdpClient> ConnectTcpAsync(string host, int port, CancellationToken cancellation = default) =>
        throw NotPorted.Yet();

    public static Task<RdpClient> ConnectUnixAsync(string socketPath, CancellationToken cancellation = default) =>
        throw NotPorted.Yet();

    /// <summary>Raised once, on a thread-pool thread, when the connection goes away for any reason.</summary>
    public event Action? Closed;

    /// <summary>Finds the parent-process console, the actor that evaluates privileged script.</summary>
    public Task AttachToParentProcessAsync() => throw NotPorted.Yet();

    /// <summary>
    /// Evaluates <paramref name="script"/> with system privileges in Gecko's parent process and
    /// returns its value. Promises are awaited. Strings, numbers and booleans come back as
    /// <see cref="JsonValue"/>; <c>undefined</c>/<c>null</c> as null; long strings are fetched in
    /// full; any other object as its grip (<see cref="JsonObject"/>).
    /// </summary>
    /// <exception cref="RdpException">Remote: the script threw. Closed: the connection is gone.
    /// Protocol: not attached, or an unexpected reply.</exception>
    public Task<JsonNode?> EvaluateAsync(string script) => throw NotPorted.Yet();

    public void Close() => throw NotPorted.Yet();

    public void Dispose() => Close();

    // Referenced so the contract compiles without warnings until implemented.
    private void RaiseClosed() => Closed?.Invoke();
}
