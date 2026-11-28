using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.App;

/// <summary>
/// Makes sure only one Mosaic runs on a given data folder (both engines keep a profile there
/// that cannot be shared between processes), and hands what a second launch was asked to open
/// to the Mosaic that is already running.
/// </summary>
/// <remarks>
/// <para>The guard is a named mutex derived from the data folder, so a Mosaic pointed at a
/// different folder (<c>MOSAIC_DATA_DIR</c>, as tests do) is not affected. Create and dispose it
/// on the same thread: a mutex belongs to the thread that acquired it.</para>
/// <para>A second launch (a link opened from another app once Mosaic is the default browser, a
/// click on the taskbar's pinned icon) connects to the first one's pipe, named after the same
/// digest and open to the current user only, and writes one JSON line:
/// <c>{"activate": true, "payload": "…"}</c>. The payload is opaque here (<see cref="Activation"/>
/// makes and applies it); the first instance applies it and comes to the front. The second launch grants it the right to take the
/// foreground first (<c>AllowSetForegroundWindow</c>), which Windows would otherwise refuse.</para>
/// </remarks>
internal sealed partial class SingleInstance : IDisposable
{
    private const int SW_RESTORE = 9;
    private static readonly TimeSpan ClientPatience = TimeSpan.FromSeconds(5);

    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stopping = new();

    public SingleInstance(string dataRoot)
    {
        // Windows paths are case-insensitive; two spellings of one folder must collide.
        var folder = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)), 0, 8);
        mutex = new Mutex(initiallyOwned: true, $@"Local\Mosaic.Instance.{digest}", out var createdNew);
        pipeName = $"Mosaic.Instance.{digest}";
        IsFirst = createdNew;
    }

    /// <summary>True when no other Mosaic is running on the data folder.</summary>
    public bool IsFirst { get; }

    /// <summary>
    /// The addresses among command-line arguments: web and file URLs, and paths of files that
    /// exist (as file URLs). Switches (<c>--…</c>) are not addresses.
    /// </summary>
    public static IReadOnlyList<string> AddressesIn(IEnumerable<string> arguments)
    {
        var addresses = new List<string>();
        foreach (var argument in arguments)
        {
            if (argument.Length == 0 || argument.StartsWith("--", StringComparison.Ordinal)) continue;
            if (Uri.TryCreate(argument, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" or "file" or "about" && !uri.IsUnc)
            {
                addresses.Add(uri.IsFile ? uri.AbsoluteUri : argument);
            }
            else if (File.Exists(argument))
            {
                addresses.Add(new Uri(Path.GetFullPath(argument)).AbsoluteUri);
            }
        }
        return addresses;
    }

    /// <summary>
    /// Second instance: passes <paramref name="payload"/> to the running Mosaic and, if
    /// <paramref name="activate"/>, asks it to come to the front. Falls back to bringing its
    /// window forward directly when it does not answer.
    /// </summary>
    public void ForwardToRunningInstance(string payload, bool activate)
    {
        // Only a process that may set the foreground window itself can pass that on; a launch
        // by the user can.
        if (activate) AllowSetForegroundWindow(ASFW_ANY);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect((int)ClientPatience.TotalMilliseconds);
            var request = new JsonObject
            {
                ["activate"] = activate,
                ["payload"] = payload,
            };
            var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
            pipe.Write(bytes);
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            reader.ReadLine(); // "ok", once the first instance has taken it
            return;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[SingleInstance] The running Mosaic did not take the request: {error.Message}");
        }
        if (activate) ActivateRunningInstance();
    }

    /// <summary>
    /// First instance: serves second launches until disposed. <paramref name="received"/> runs
    /// on the calling (UI) thread with the payload and whether to come to the front.
    /// </summary>
    public void Listen(Action<string, bool> received) => _ = ListenAsync(received);

    private async Task ListenAsync(Action<string, bool> received)
    {
        var stop = stopping.Token;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(stop);
                patience.CancelAfter(ClientPatience);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync(patience.Token);
                if (line is not null && Parse(line) is { } request)
                {
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes("ok\n"), patience.Token);
                    await pipe.FlushAsync(patience.Token);
                    received(request.Payload, request.Activate);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A client that vanished or took too long; serve the next one.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private static (string Payload, bool Activate)? Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject request) return null;
            bool activate = request["activate"] is JsonValue value && value.TryGetValue(out bool flag) && flag;
            var payload = request["payload"] is JsonValue text && text.TryGetValue(out string? s) ? s : "";
            return (payload, activate);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Brings the window of the Mosaic that is already running to the front.</summary>
    public static void ActivateRunningInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == 0) continue;
                if (IsIconic(process.MainWindowHandle)) ShowWindow(process.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    private const uint ASFW_ANY = unchecked((uint)-1);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}
