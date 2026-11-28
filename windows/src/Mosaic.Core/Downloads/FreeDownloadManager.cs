using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Mosaic.Core.Downloads;

/// <summary>Where Free Download Manager is installed.</summary>
/// <param name="Program">Path of <c>fdm.exe</c>.</param>
/// <param name="NativeHost">Path of <c>wenativehost.exe</c>, FDM's browser-integration helper, when present.</param>
/// <param name="Version">"6.36.0.7128", when known.</param>
public sealed record FdmInstallation(string Program, string? NativeHost, string? Version);

/// <summary>
/// Free Download Manager 6 on Windows (<c>Softdeluxe\Free Download Manager</c>).
/// </summary>
/// <remarks>
/// <para>Port of <c>FreeDownloadManager.swift</c>. Downloads are handed over through FDM's own
/// browser-integration helper, <c>wenativehost.exe</c> (the native messaging host
/// <c>org.freedownloadmanager.fdm5.cnh</c> that FDM's Chrome, Edge and Firefox extensions talk to),
/// which accepts the URL plus the referrer, the User-Agent and, if the user allows it, cookies.
/// FDM then shows its "New download" window, where the user confirms. If the helper is missing
/// or does not answer, Mosaic runs <c>fdm.exe &lt;url&gt;</c> instead, which passes the address
/// only. Mosaic never changes FDM's settings.</para>
/// <para>FDM is found through the native messaging host's registration (the manifest's
/// <c>path</c>), the Inno Setup uninstall entry (<c>InstallLocation</c>, <c>DisplayVersion</c>),
/// then the usual folders: <c>%ProgramFiles%\Softdeluxe\Free Download Manager</c> (all users) and
/// <c>%LOCALAPPDATA%\Softdeluxe\Free Download Manager</c> (per user).</para>
/// </remarks>
public sealed class FreeDownloadManager : IFreeDownloadManager
{
    public const string NativeHostName = "org.freedownloadmanager.fdm5.cnh";
    private const string ProgramName = "fdm.exe";
    private const string HostProgramName = "wenativehost.exe";

    private readonly Func<FdmInstallation?> locate;
    private readonly Func<FdmInstallation, IFdmHostProcess?> launchHost;
    private readonly Func<FdmInstallation, string, bool> openUrl;
    private FdmInstallation? installation;
    private bool located;

    /// <param name="locate">Finds FDM (default: <see cref="Locate"/>, Windows registry and folders).</param>
    /// <param name="launchHost">Starts the helper (default: the real process).</param>
    /// <param name="openUrl">Passes a URL to <c>fdm.exe</c> (default: starts it with the URL).</param>
    public FreeDownloadManager(
        Func<FdmInstallation?>? locate = null,
        Func<FdmInstallation, IFdmHostProcess?>? launchHost = null,
        Func<FdmInstallation, string, bool>? openUrl = null)
    {
        this.locate = locate ?? Locate;
        this.launchHost = launchHost ?? FdmHostProcess.Start;
        this.openUrl = openUrl ?? OpenWithProgram;
    }

    /// <summary>Lets FDM's window come to the front (the app calls <c>AllowSetForegroundWindow</c>).</summary>
    public Action? AllowForeground { get; set; }

    /// <summary>How long to wait for the helper's handshake reply.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How long FDM may take to answer (the user confirming in its window).</summary>
    public TimeSpan OutcomeTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public FdmInstallation? Installation
    {
        get
        {
            if (!located) Refresh();
            return installation;
        }
    }

    public bool IsInstalled => Installation is not null;
    public string? Version => Installation?.Version;

    public void Refresh()
    {
        located = true;
        try
        {
            installation = locate();
        }
        catch (Exception)
        {
            installation = null;
        }
    }

    public async Task<FdmRoute> SendAsync(FdmDownloadRequest request, bool bringToFront, Action<FdmOutcome> onOutcome)
    {
        Refresh();
        if (installation is not { } found) throw new InvalidOperationException("Free Download Manager is not installed.");
        if (bringToFront) AllowForeground?.Invoke();
        if (found.NativeHost is not null && launchHost(found) is { } host)
        {
            var session = new FdmHostSession(host);
            if (await session.DeliverAsync(request, HandshakeTimeout))
            {
                var context = SynchronizationContext.Current;
                _ = Task.Run(async () =>
                {
                    var outcome = await session.WaitForOutcomeAsync(OutcomeTimeout).ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                    if (outcome is null) return;
                    if (context is null) onOutcome(outcome);
                    else context.Post(_ => onOutcome(outcome), null);
                });
                return FdmRoute.Integration;
            }
            await session.DisposeAsync();
        }
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _)) throw new InvalidOperationException("Free Download Manager could not be opened: invalid address");
        if (!openUrl(found, request.Url)) throw new InvalidOperationException("Free Download Manager could not be opened.");
        return FdmRoute.OpenUrl;
    }

    private static bool OpenWithProgram(FdmInstallation found, string url)
    {
        try
        {
            var start = new ProcessStartInfo(found.Program) { UseShellExecute = false };
            start.ArgumentList.Add(url);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    // ---- Finding FDM ----

    /// <summary>Finds FDM on this PC (Windows only; null elsewhere or when it is not installed).</summary>
    public static FdmInstallation? Locate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? host = NativeHostFromRegistry();
        var (location, version) = UninstallEntry();
        var folders = new List<string>();
        if (host is not null && Path.GetDirectoryName(host) is { } hostFolder) folders.Add(hostFolder);
        if (location is not null) folders.Add(location);
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (root.Length > 0) folders.Add(Path.Combine(root, "Softdeluxe", "Free Download Manager"));
        }
        return Resolve(folders, host, version, File.Exists, FileVersion);
    }

    /// <summary>Picks the installation from candidate folders (testable part of <see cref="Locate"/>).</summary>
    internal static FdmInstallation? Resolve(
        IEnumerable<string> folders, string? registeredHost, string? version, Func<string, bool> exists,
        Func<string, string?> fileVersion)
    {
        foreach (var folder in folders)
        {
            var program = Path.Combine(folder, ProgramName);
            if (!exists(program)) continue;
            var host = registeredHost is not null && exists(registeredHost)
                ? registeredHost
                : exists(Path.Combine(folder, HostProgramName)) ? Path.Combine(folder, HostProgramName) : null;
            return new FdmInstallation(program, host, version ?? fileVersion(program));
        }
        return null;
    }

    /// <summary>The helper named by a native messaging manifest (its <c>path</c> may be relative to the manifest).</summary>
    internal static string? HostFromManifest(string manifestPath, string manifestText)
    {
        try
        {
            if (JsonNode.Parse(manifestText) is not JsonObject manifest ||
                manifest["path"] is not JsonValue value || !value.TryGetValue(out string? path) || string.IsNullOrEmpty(path))
            {
                return null;
            }
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath) ?? "", path));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? NativeHostFromRegistry()
    {
        string[] browsers = [@"Software\Google\Chrome", @"Software\Microsoft\Edge", @"Software\Mozilla", @"Software\Chromium"];
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var browser in browsers)
            {
                try
                {
                    using var key = hive.OpenSubKey($@"{browser}\NativeMessagingHosts\{NativeHostName}");
                    if (key?.GetValue(null) is not string manifest || !File.Exists(manifest)) continue;
                    if (HostFromManifest(manifest, File.ReadAllText(manifest)) is { } host && File.Exists(host)) return host;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }
        }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string? Location, string? Version) UninstallEntry()
    {
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, path) in new[]
        {
            (Registry.CurrentUser, uninstall),
            (Registry.LocalMachine, uninstall),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display ||
                        !display.StartsWith("Free Download Manager", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var location = (entry.GetValue("InstallLocation") as string)?.Trim('"', ' ');
                    if (string.IsNullOrEmpty(location) && entry.GetValue("DisplayIcon") is string icon)
                    {
                        location = Path.GetDirectoryName(icon.Split(',')[0].Trim('"'));
                    }
                    return (string.IsNullOrEmpty(location) ? null : location, entry.GetValue("DisplayVersion") as string);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return (null, null);
    }

    private static string? FileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch (Exception error) when (error is IOException or FileNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>The helper's standard input and output (a process, or pipes in tests).</summary>
public interface IFdmHostProcess : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
}

internal sealed class FdmHostProcess(Process process) : IFdmHostProcess
{
    public Stream Input => process.StandardInput.BaseStream;
    public Stream Output => process.StandardOutput.BaseStream;

    public static IFdmHostProcess? Start(FdmInstallation installation)
    {
        if (installation.NativeHost is not { } host) return null;
        try
        {
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(host) ?? "",
            };
            var process = Process.Start(start);
            if (process is null) return null;
            // Nobody reads it; drain it so the helper never blocks on a full pipe.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return new FdmHostProcess(process);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(2000)) process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>One conversation with <c>wenativehost</c> over its standard input and output.</summary>
internal sealed class FdmHostSession : IAsyncDisposable
{
    private readonly IFdmHostProcess host;
    private readonly object gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonObject?>> waiters = [];
    private readonly Dictionary<string, JsonObject> received = [];
    private readonly Task reading;
    private bool ended;
    private int disposed;

    public FdmHostSession(IFdmHostProcess host)
    {
        this.host = host;
        reading = Task.Run(ReadAsync);
    }

    /// <summary>Handshake, then the download. True when FDM's helper took it.</summary>
    public async Task<bool> DeliverAsync(FdmDownloadRequest request, TimeSpan handshakeTimeout)
    {
        if (!await WriteAsync(FdmNativeMessage.Handshake("1"))) return false;
        var reply = await ResponseAsync("1", handshakeTimeout);
        if (reply is null || FdmNativeMessage.Text(reply["error"]) is { Length: > 0 }) return false;
        return await WriteAsync(FdmNativeMessage.CreateDownloads("2", [request]));
    }

    /// <summary>FDM's answer to the download: after the user confirms or cancels (or at once if FDM cannot fetch it).</summary>
    public async Task<FdmOutcome?> WaitForOutcomeAsync(TimeSpan timeout) =>
        await ResponseAsync("2", timeout) is { } reply ? FdmNativeMessage.Outcome(reply) : null;

    private async Task<bool> WriteAsync(JsonObject message)
    {
        lock (gate)
        {
            if (ended) return false;
        }
        try
        {
            var data = FdmNativeMessage.Frame(message);
            await host.Input.WriteAsync(data).ConfigureAwait(false);
            await host.Input.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<JsonObject?> ResponseAsync(string id, TimeSpan timeout)
    {
        TaskCompletionSource<JsonObject?> waiter;
        lock (gate)
        {
            if (received.Remove(id, out var message)) return message;
            if (ended) return null;
            waiter = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters[id] = waiter;
        }
        try
        {
            return await waiter.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (gate) waiters.Remove(id);
            return null;
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new List<byte>();
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await host.Output.ReadAsync(chunk).ConfigureAwait(false);
                if (count <= 0) break;
                buffer.AddRange(chunk.AsSpan(0, count).ToArray());
                foreach (var message in FdmNativeMessage.TakeMessages(buffer))
                {
                    // Requests from the helper (cookie queries) are declined: Mosaic passes cookies
                    // only with the download itself, and only when the user allowed it.
                    if (FdmNativeMessage.IsRequest(message))
                    {
                        if (message["id"] is not null) await WriteAsync(FdmNativeMessage.Decline(message)).ConfigureAwait(false);
                        continue;
                    }
                    if (FdmNativeMessage.Text(message["id"]) is not { } id) continue;
                    TaskCompletionSource<JsonObject?>? waiter;
                    lock (gate)
                    {
                        if (!waiters.Remove(id, out waiter)) received[id] = message;
                    }
                    waiter?.TrySetResult(message);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            List<TaskCompletionSource<JsonObject?>> pending;
            lock (gate)
            {
                ended = true;
                pending = [.. waiters.Values];
                waiters.Clear();
            }
            foreach (var waiter in pending) waiter.TrySetResult(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await host.DisposeAsync().ConfigureAwait(false);
        try
        {
            await reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }
}
