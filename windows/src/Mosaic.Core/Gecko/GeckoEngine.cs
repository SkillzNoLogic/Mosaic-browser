using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Gecko;

public sealed class GeckoEngineOptions
{
    /// <summary>Profile directory. Default: <see cref="AppPaths.GeckoProfile"/>.</summary>
    public string ProfileDirectory { get; init; } = AppPaths.GeckoProfile;

    /// <summary>Folder containing <c>agent.sys.mjs</c>. Default: <see cref="AppPaths.GeckoAgent"/>.</summary>
    public string AgentDirectory { get; init; } = AppPaths.GeckoAgent;

    /// <summary>Use this runtime instead of <see cref="GeckoRuntime.Locate"/>.</summary>
    public GeckoRuntime? Runtime { get; init; }

    /// <summary>Extra command-line arguments for Gecko (tests pass <c>--headless</c>).</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>How often the agent's state is polled.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Called on a thread-pool thread with the ID of the process Mosaic launched, immediately after
    /// launch. The app uses it to tie that process's lifetime to its own (a kill-on-close job object).
    /// </summary>
    public Action<int>? ProcessLaunched { get; init; }

    /// <summary>How long each stage of starting (debugging server, browser window) may take.</summary>
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long to keep looking for the debugging server after the launched process has exited
    /// successfully; see the launcher-process notes on <see cref="GeckoEngine"/>.
    /// </summary>
    internal TimeSpan HandOffGrace { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Why the engine could not be started.</summary>
public enum GeckoFailure
{
    NoRuntime,
    AgentMissing,
    AgentOutOfDate,
    LaunchFailed,
    ProfileInUse,
    ExitedDuringStartup,
    StartTimedOut,
    WrongEngine,
}

/// <summary>A failure to start Gecko. <see cref="Exception.Message"/> is user-presentable.</summary>
public sealed class GeckoEngineException(GeckoFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public GeckoFailure Failure { get; } = failure;
}

/// <summary>The real <see cref="IGeckoEngine"/>.</summary>
/// <remarks>
/// <para>
/// Port of <c>Sources/Mosaic/Engines/GeckoEngine.swift</c> minus the macOS window docking
/// (on Windows the app embeds <see cref="WindowHandle"/> directly). Windows specifics: the
/// debugging server listens on a free loopback TCP port; Firefox's launcher process means the
/// launched PID is not necessarily the browser's, so <see cref="ProcessId"/> comes from the agent;
/// an engine left behind by a previous Mosaic that crashed must be terminated before starting.
/// </para>
/// <para>
/// <b>Debugging port.</b> Gecko's debugging server takes a socket path on macOS and Linux but
/// only a TCP port on Windows, so the engine picks a free loopback port and passes it to
/// <c>--start-debugger-server</c>. Gecko binds it to the loopback interface only
/// (<c>devtools.debugger.force-local</c>, pinned in the profile). The server does not
/// authenticate: any process running on the machine, as any user, can connect to the port while
/// Gecko runs and gain the same control over the engine that Mosaic has. That is a known
/// limitation of this prototype, accepted rather than solved here.
/// </para>
/// <para>
/// <b>Launcher process.</b> On Windows <c>firefox.exe</c> normally runs first as a small
/// "launcher process" that creates the real browser process as its child and, by default, exits
/// as soon as that child is up (<c>LauncherMain</c> in browser/app/winlauncher/LauncherProcessWin.cpp).
/// The process Mosaic starts is therefore not Gecko, and its exit says nothing by itself.
/// The documented way to switch the launcher off is the <c>browser.launcherProcess.enabled</c>
/// preference, which Gecko mirrors into the registry under a value named after the executable's
/// path (<c>HKCU\Software\Mozilla\Firefox\Launcher</c>, toolkit/xre/LauncherRegistryInfo.cpp):
/// it would switch the launcher off for the user's own Firefox too whenever Mosaic runs a shared
/// installation, and it only takes effect from the following start. So the launcher is left on,
/// and the engine instead passes <c>--wait-for-browser</c>, the switch automation uses, which
/// makes the launcher put the browser in a kill-on-close job, wait for it, and exit with its exit
/// code. That gives the launched process the same lifetime as Gecko in both directions: it ends
/// when Gecko ends, and ending it (which is what the app's job object does if Mosaic dies) ends
/// Gecko. The switch also stops an elevated launcher from re-launching itself through the shell,
/// which would detach Gecko from Mosaic altogether.
/// Nothing here relies on that being the whole story, though (a fork may lack the switch, the
/// launcher may already be off for that installation, Gecko can restart itself): what "running" means is
/// decided by the debugging connection alone. <see cref="ProcessId"/> and
/// <see cref="WindowHandle"/> come from the agent, a launched process that exits successfully
/// before the debugging server is up is given <see cref="GeckoEngineOptions.HandOffGrace"/> to be
/// succeeded by a browser, and Gecko is taken to have quit when the connection closes.
/// </para>
/// <para>
/// <b>Whose Gecko answers.</b> Every launch carries a random token in the
/// <c>MOSAIC_ENGINE_TOKEN</c> environment variable, which the browser process inherits. The
/// script that loads the agent first checks it, so the engine never drives a Gecko other than the
/// one it launched: not one already on the profile that was handed this launch's command line
/// (Gecko's command-line handler opens a debugging server for a forwarded
/// <c>--start-debugger-server</c> too, devtools/startup/DevToolsStartup.sys.mjs), and not some
/// other program that happened to get the port.
/// </para>
/// <para>
/// <b>Leftover engines.</b> See <see cref="EngineRecord"/> for how an engine orphaned by a crashed
/// Mosaic is recognised and terminated without any risk to other processes.
/// </para>
/// <para>
/// <b>Events.</b> <see cref="Stopped"/> is raised whenever the engine leaves
/// <see cref="GeckoStatus.Running"/>, whether Gecko went away by itself or <see cref="Stop"/> was
/// called (the macOS engine raises it only in the first case). An exception thrown by
/// <see cref="OnStarted"/> is reported by <see cref="StartAsync"/> but does not stop Gecko.
/// </para>
/// </remarks>
public sealed class GeckoEngine : IGeckoEngine
{
    /// <summary>Environment variable carrying the launch token into Gecko.</summary>
    internal const string TokenVariable = "MOSAIC_ENGINE_TOKEN";

    private const string AgentExpression = """ChromeUtils.importESModule("resource://mosaic/agent.sys.mjs").Agent""";

    /// <summary>The first agent version that reports <c>pid</c> and <c>windowHandle</c> and can <c>quit()</c>.</summary>
    private const int MinimumAgentVersion = 2;

    /// <summary>What the bootstrap script throws when the launch token does not match.</summary>
    private const string WrongEngineMarker = "mosaic-wrong-engine";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>How long Gecko gets to quit by itself before it is terminated.</summary>
    private static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long a terminated process gets to actually disappear.</summary>
    private static readonly TimeSpan KillSettle = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan StaleEngineTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to try connecting even though the port does not look open; see <see cref="ConnectAsync"/>.
    /// </summary>
    private static readonly TimeSpan BlindConnectInterval = TimeSpan.FromSeconds(2);

    private readonly GeckoEngineOptions _options;

    /// <summary>Where events are raised; null means "wherever they happen" (no UI thread to return to).</summary>
    private readonly SynchronizationContext? _context;

    private readonly Lock _gate = new();
    private GeckoStatus _status = GeckoStatus.Stopped;
    private string? _failure;
    private GeckoRuntime? _runtime;

    /// <summary>The launch that is starting or running, if any.</summary>
    private Session? _session;

    /// <summary>Launches that have been let go of and whose processes are still being seen off.</summary>
    private readonly List<Session> _closing = [];

    /// <summary>Completes when every launch in <see cref="_closing"/> has been dealt with. Never faults.</summary>
    private Task _shutdowns = Task.CompletedTask;

    /// <summary>Everything that belongs to one launch of Gecko. Mutable members are guarded by <see cref="_gate"/>.</summary>
    private sealed class Session
    {
        public string Token { get; } = Guid.NewGuid().ToString("N");

        /// <summary>Cancelled when the launch is let go of, for any reason.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Completes when Gecko is running or the attempt has failed. Assigned before the session is published.</summary>
        public Task Launch = Task.CompletedTask;

        /// <summary>What <see cref="StartAsync"/> hands out: <see cref="Launch"/> followed by <see cref="OnStarted"/>.</summary>
        public Task Start = Task.CompletedTask;

        public Task? Shutdown;
        public string? ProfileDirectory;
        public int Port;

        /// <summary>The process Mosaic created.</summary>
        public Process? Launched;

        /// <summary>Gecko's browser process, when the agent reported one other than <see cref="Launched"/>.</summary>
        public Process? Browser;

        public RdpClient? Client;
        public int? BrowserProcessId;
        public nint WindowHandle;
    }

    public GeckoEngine(GeckoEngineOptions? options = null)
    {
        _options = options ?? new GeckoEngineOptions();
        _context = SynchronizationContext.Current;
        _runtime = _options.Runtime ?? GeckoRuntime.Locate();
    }

    public GeckoStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public string? FailureMessage
    {
        get { lock (_gate) return _status == GeckoStatus.Failed ? _failure : null; }
    }

    public GeckoRuntime? Runtime
    {
        get { lock (_gate) return _runtime; }
    }

    public int? ProcessId
    {
        get { lock (_gate) return _status == GeckoStatus.Running ? _session?.BrowserProcessId : null; }
    }

    public nint WindowHandle
    {
        get { lock (_gate) return _status == GeckoStatus.Running ? _session?.WindowHandle ?? 0 : 0; }
    }

    /// <summary>The loopback port of the running Gecko's debugging server. For tests.</summary>
    internal int? DebuggerPort
    {
        get { lock (_gate) return _status == GeckoStatus.Running ? _session?.Port : null; }
    }

    public event Action? StatusChanged;
    public event Action<GeckoSnapshot>? SnapshotReceived;
    public event Action? Stopped;
    public Func<Task>? OnStarted { get; set; }

    // ---- Lifecycle ------------------------------------------------------------------------

    public Task StartAsync()
    {
        Session session;
        lock (_gate)
        {
            if (_status == GeckoStatus.Running) return Task.CompletedTask;
            if (_status == GeckoStatus.Starting && _session is { } starting) return starting.Start;

            session = new Session();
            var earlier = _shutdowns;
            // Both run on the thread pool: nothing about starting may block or re-enter the caller.
            session.Launch = Task.Run(() => LaunchAsync(session, earlier));
            session.Start = Task.Run(() => FinishStartAsync(session));
            _session = session;
            _status = GeckoStatus.Starting;
            _failure = null;
        }
        Raise(() => StatusChanged?.Invoke());
        return session.Start;
    }

    private async Task FinishStartAsync(Session session)
    {
        try
        {
            await session.Launch.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            GeckoEngineException? failure = null;
            lock (_gate)
            {
                // Never leave a half-started Gecko running in the background.
                BeginShutdown(session, askToQuit: false, grace: TimeSpan.Zero);
                if (_session == session)
                {
                    failure = Describe(error);
                    _session = null;
                    _status = GeckoStatus.Failed;
                    _failure = failure.Message;
                }
            }
            // Not current any more means Stop() got here first and has already said so.
            if (failure is null) throw new OperationCanceledException("Gecko was stopped while it was starting.");
            Raise(() => StatusChanged?.Invoke());
            throw failure;
        }

        await InvokeOnStartedAsync().ConfigureAwait(false);
    }

    private async Task LaunchAsync(Session session, Task earlierShutdowns)
    {
        var cancellation = session.Cancellation.Token;

        // A Gecko this engine is still shutting down holds the profile the new one needs.
        await earlierShutdowns.WaitAsync(cancellation).ConfigureAwait(false);

        var runtime = _options.Runtime ?? GeckoRuntime.Locate();
        lock (_gate) _runtime = runtime;
        if (runtime is null)
        {
            throw new GeckoEngineException(GeckoFailure.NoRuntime,
                "No Gecko runtime was found. Install Firefox (or Firefox ESR) and start Mosaic again.");
        }

        var agentDirectory = Path.GetFullPath(_options.AgentDirectory);
        if (!File.Exists(Path.Combine(agentDirectory, "agent.sys.mjs")))
        {
            throw new GeckoEngineException(GeckoFailure.AgentMissing,
                $"Mosaic's Gecko agent is missing from {agentDirectory}. Reinstall Mosaic.");
        }

        var profile = Path.GetFullPath(_options.ProfileDirectory);
        lock (_gate) session.ProfileDirectory = profile;

        await MakeProfileAvailableAsync(profile, cancellation).ConfigureAwait(false);
        GeckoProfile.Prepare(profile);

        var port = FreeLoopbackPort();
        var launched = StartProcess(runtime, profile, port, session.Token);
        lock (_gate)
        {
            session.Launched = launched;
            session.Port = port;
        }
        NotifyProcessLaunched(launched);
        var record = new EngineRecord(
            session.Token, ProcessIdentity.Current, ProcessIdentity.Of(launched) ?? new ProcessIdentity(launched.Id, 0), null);
        record.Save(profile);

        // Mosaic may have been told to stop (or quit) while the process was being created.
        // Checked only now, with the process on the session, so that whoever asked is certain
        // to find it there and end it: a launch in flight must not orphan Gecko.
        cancellation.ThrowIfCancellationRequested();

        // 1. The debugging server appears once Gecko's startup has got far enough.
        var client = await ConnectAsync(launched, port, cancellation).ConfigureAwait(false);
        lock (_gate) session.Client = client;
        client.Closed += () => OnConnectionClosed(session);

        // 2. Load the agent into the parent process.
        await client.AttachToParentProcessAsync().WaitAsync(cancellation).ConfigureAwait(false);
        JsonNode? loaded;
        try
        {
            loaded = await client.EvaluateAsync(BootstrapScript(agentDirectory, session.Token))
                .WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (RdpException error) when (error.Kind == RdpErrorKind.Remote && error.Message.Contains(WrongEngineMarker))
        {
            throw new GeckoEngineException(GeckoFailure.WrongEngine,
                "A different Gecko process answered instead of the one Mosaic started. " +
                "Another Gecko is probably already using Mosaic's engine profile; close it and try again.", error);
        }
        if (DecodeResult(loaded) is not JsonObject description ||
            description["agent"] is not JsonValue agentVersion || !agentVersion.TryGetValue(out int agent) ||
            agent < MinimumAgentVersion)
        {
            throw new GeckoEngineException(GeckoFailure.AgentOutOfDate,
                $"The Gecko agent in {agentDirectory} is older than this version of Mosaic needs. Reinstall Mosaic.");
        }

        // 3. Wait for the browser window, then strip its chrome.
        var clock = Stopwatch.StartNew();
        while (!IsTrue(await client.EvaluateAsync(AgentCall("setup")).WaitAsync(cancellation).ConfigureAwait(false)))
        {
            if (clock.Elapsed > _options.StartupTimeout) throw StartTimedOut("browser window");
            await Task.Delay(RetryInterval, cancellation).ConfigureAwait(false);
        }

        // 4. Only now is there a window to report a handle for.
        var (processId, windowHandle) = ParseVersion(DecodeResult(
            await client.EvaluateAsync(AgentCall("version")).WaitAsync(cancellation).ConfigureAwait(false)));
        var browser = processId == launched.Id ? null : OpenProcess(processId);

        lock (_gate)
        {
            if (_session != session)
            {
                browser?.Dispose();
                throw new OperationCanceledException(cancellation);
            }
            session.Browser = browser;
            session.BrowserProcessId = processId;
            session.WindowHandle = windowHandle;
            _status = GeckoStatus.Running;
        }
        if (browser is not null && ProcessIdentity.Of(browser) is { } identity)
        {
            (record with { Browser = identity }).Save(profile);
        }
        Raise(() => StatusChanged?.Invoke());

        // The connection may have dropped after the last reply and before "running" was set,
        // when the handler above still took it for part of a failing start.
        if (client.IsClosed) OnConnectionClosed(session);

        _ = Task.Run(() => PollAsync(session, client));
    }

    /// <summary>
    /// Makes sure no other Gecko holds the profile: terminates this Mosaic's own leftover engine,
    /// and refuses to start beside anything else.
    /// </summary>
    private static async Task MakeProfileAvailableAsync(string profile, CancellationToken cancellation)
    {
        var stale = await EngineRecord.TerminateStaleAsync(profile, StaleEngineTimeout, cancellation).ConfigureAwait(false);
        if (stale == StaleEngine.InUse)
        {
            throw new GeckoEngineException(GeckoFailure.ProfileInUse,
                "Another running Mosaic is already using the Mozilla engine. Close it and try again.");
        }

        if (stale == StaleEngine.Survived)
        {
            throw new GeckoEngineException(GeckoFailure.ProfileInUse,
                "A Mozilla engine left over from an earlier run of Mosaic could not be closed. " +
                "End its firefox process, or sign out and back in, then try again.");
        }

        // A terminated process's files are released a moment after it is reported gone.
        var clock = Stopwatch.StartNew();
        while (GeckoProfile.IsLocked(profile))
        {
            if (stale != StaleEngine.Terminated || clock.Elapsed > StaleEngineTimeout)
            {
                // Something holds the profile that Mosaic has no record of launching, so it may
                // not be touched. A Gecko started beside it would not get the profile, and
                // without --headless would say so in a Firefox dialog; see EngineRecord.
                throw new GeckoEngineException(GeckoFailure.ProfileInUse,
                    $"Another Gecko process is already using Mosaic's engine profile ({profile}). Close it and try again.");
            }
            await Task.Delay(RetryInterval, cancellation).ConfigureAwait(false);
        }
    }

    private Process StartProcess(GeckoRuntime runtime, string profile, int port, string token)
    {
        var start = new ProcessStartInfo(runtime.ExecutablePath)
        {
            UseShellExecute = false,
            // Gecko writes little, but must never block on a full pipe or scribble on a console
            // Mosaic's own process happens to have; both streams are read and discarded below.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(profile);
        // Current Gecko decides "one instance per profile" by itself and ignores this switch;
        // older builds and forks still need it to stay away from the user's running browser.
        start.ArgumentList.Add("--no-remote");
        if (OperatingSystem.IsWindows())
        {
            // See the launcher-process notes on this class.
            start.ArgumentList.Add("--wait-for-browser");
        }
        start.ArgumentList.Add("--start-debugger-server");
        start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        foreach (var argument in _options.ExtraArguments) start.ArgumentList.Add(argument);

        start.Environment[TokenVariable] = token;
        // If Gecko crashes, keep the crash dump but do not put up Firefox's crash reporter, whose
        // "restart" button would start a bare Firefox on Mosaic's profile.
        start.Environment["MOZ_CRASHREPORTER_NO_REPORT"] = "1";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process was created");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            throw new GeckoEngineException(GeckoFailure.LaunchFailed,
                $"{runtime.Name} could not be started ({runtime.ExecutablePath}): {error.Message}", error);
        }

        // Read as raw streams, not with BeginOutputReadLine: Gecko's child processes inherit these
        // pipes and can outlive it, and waiting for the launched process must not wait for them.
        _ = Drain(process.StandardOutput.BaseStream);
        _ = Drain(process.StandardError.BaseStream);
        return process;

        static async Task Drain(Stream stream)
        {
            try
            {
                await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The pipe went away with the process.
            }
        }
    }

    private void NotifyProcessLaunched(Process launched)
    {
        if (_options.ProcessLaunched is not { } callback) return;
        try
        {
            callback(launched.Id);
        }
        catch (Exception)
        {
            // Tying lifetimes together is the app's safety net; failing to must not stop Gecko
            // from starting, and the leftover-engine check covers the case it was there for.
        }
    }

    /// <summary>
    /// Connects to Gecko's debugging server once it is listening.
    /// </summary>
    /// <remarks>
    /// On Windows a connection attempt to a loopback port nobody listens on is not refused at
    /// once: the stack retries for about two seconds before giving up. Polling by connecting would
    /// add that much to every start, so the port is looked up in the system's table of listeners
    /// first and a connection is attempted only when it is there. In case that table is
    /// unavailable or wrong, an attempt is still made every <see cref="BlindConnectInterval"/>.
    /// </remarks>
    private async Task<RdpClient> ConnectAsync(Process launched, int port, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var lastAttempt = TimeSpan.Zero;
        TimeSpan? exited = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (IsListening(port) != false || clock.Elapsed - lastAttempt >= BlindConnectInterval)
            {
                lastAttempt = clock.Elapsed;
                try
                {
                    return await RdpClient.ConnectTcpAsync(IPAddress.Loopback.ToString(), port, cancellation).ConfigureAwait(false);
                }
                catch (RdpException error) when (error.Kind == RdpErrorKind.ConnectFailed)
                {
                    // Not listening yet.
                }
            }

            if (EngineRecord.HasExited(launched))
            {
                // A failure exit is Gecko failing to start. A successful one is not conclusive:
                // it is also how a launcher process, or a Gecko restarting itself, leaves the
                // stage to the real browser process. Give that one a while to appear.
                exited ??= clock.Elapsed;
                if (ExitCode(launched) != 0)
                {
                    throw new GeckoEngineException(GeckoFailure.ExitedDuringStartup, "Gecko quit while it was starting.");
                }
                if (clock.Elapsed - exited > _options.HandOffGrace)
                {
                    // Exiting politely without ever starting is what Gecko does when another
                    // Gecko has the profile (observed with Firefox ESR 153: "A copy of Firefox
                    // is already open", exit code 0, about five seconds after launch).
                    throw new GeckoEngineException(GeckoFailure.ExitedDuringStartup,
                        "Gecko quit while it was starting. Another Gecko may already be using Mosaic's engine profile.");
                }
            }
            if (clock.Elapsed > _options.StartupTimeout) throw StartTimedOut("debugging server");
            await Task.Delay(RetryInterval, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Opens the process the agent just reported itself to be; null if it has gone since.</summary>
    /// <summary>
    /// Waits, at most <paramref name="timeout"/>, until no process holds the profile's lock.
    /// Returns whether it was released. Immediate where the lock cannot be observed (macOS).
    /// </summary>
    internal static async Task<bool> WaitForProfileReleaseAsync(string profile, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (GeckoProfile.IsLocked(profile))
        {
            if (clock.Elapsed >= timeout) return false;
            await Task.Delay(RetryInterval).ConfigureAwait(false);
        }
        return true;
    }

    private static Process? OpenProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int ExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return 0; // unknown; treated like a hand-off rather than assumed to be a failure
        }
    }

    /// <summary>
    /// Asks the operating system for a loopback port nobody is using. Another program could take
    /// it in the instant before Gecko binds it; Gecko then starts without a debugging server and
    /// the start fails with a timeout, or, should that program speak the protocol, with the
    /// launch-token check.
    /// </summary>
    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Whether something listens on the port; null if the system will not say.</summary>
    internal static bool? IsListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException
            or NotImplementedException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static GeckoEngineException StartTimedOut(string stage) =>
        new(GeckoFailure.StartTimedOut, $"Gecko did not finish starting ({stage}).");

    private static GeckoEngineException Describe(Exception error) => error switch
    {
        GeckoEngineException known => known,
        // The connection only closes when Gecko is going away.
        RdpException { Kind: RdpErrorKind.Closed } => new GeckoEngineException(
            GeckoFailure.ExitedDuringStartup, "Gecko quit while it was starting.", error),
        _ => new GeckoEngineException(GeckoFailure.LaunchFailed, $"Gecko could not be started: {error.Message}", error),
    };

    private async Task InvokeOnStartedAsync()
    {
        if (OnStarted is not { } handler) return;
        if (_context is null)
        {
            await handler().ConfigureAwait(false);
            return;
        }

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(
            async _ =>
            {
                try
                {
                    await handler();
                    finished.TrySetResult();
                }
                catch (Exception error)
                {
                    finished.TrySetException(error);
                }
            },
            null);
        await finished.Task.ConfigureAwait(false);
    }

    public void Stop() => BeginStop(QuitGrace);

    /// <remarks>
    /// Gecko is asked to quit and given <paramref name="timeout"/> to do so, then terminated; the
    /// call returns once its processes are gone, at worst a couple of seconds after the timeout.
    /// A launch that is still in flight is ended as soon as its process exists.
    /// </remarks>
    public void StopAndWait(TimeSpan timeout)
    {
        var shutdowns = BeginStop(timeout);
        if (shutdowns.Wait(timeout + KillSettle)) return;

        // Out of patience, including with shutdowns that an earlier Stop() started more gently.
        List<Process> remaining;
        lock (_gate) remaining = [.. _closing.SelectMany(Processes)];
        foreach (var process in remaining) EngineRecord.Kill(process);
        shutdowns.Wait(KillSettle);
    }

    /// <summary>Lets go of the current launch and returns the task that completes when Gecko is gone.</summary>
    private Task BeginStop(TimeSpan grace)
    {
        bool changed, wasRunning;
        Task shutdowns;
        lock (_gate)
        {
            wasRunning = _status == GeckoStatus.Running;
            changed = _status != GeckoStatus.Stopped;
            if (_session is { } session)
            {
                // Only a Gecko that finished starting is worth asking; one still starting has
                // nothing to save and is simply terminated.
                BeginShutdown(session, askToQuit: wasRunning, grace: wasRunning ? grace : TimeSpan.Zero);
            }
            _session = null;
            _status = GeckoStatus.Stopped;
            _failure = null;
            shutdowns = _shutdowns;
        }
        if (changed) Raise(() => StatusChanged?.Invoke());
        if (wasRunning) Raise(() => Stopped?.Invoke());
        return shutdowns;
    }

    private void OnConnectionClosed(Session session)
    {
        lock (_gate)
        {
            // While starting, the launch itself notices (its next request fails).
            if (_session != session || _status != GeckoStatus.Running) return;
            _session = null;
            _status = GeckoStatus.Stopped;
            // The connection only closes when Gecko is going away; make sure it does.
            BeginShutdown(session, askToQuit: false, grace: QuitGrace);
        }
        Raise(() => StatusChanged?.Invoke());
        Raise(() => Stopped?.Invoke());
    }

    /// <summary>Starts seeing a launch's processes off, once. Call with <see cref="_gate"/> held.</summary>
    private void BeginShutdown(Session session, bool askToQuit, TimeSpan grace)
    {
        if (session.Shutdown is not null) return;
        session.Cancellation.Cancel();
        _closing.Add(session);
        var shutdown = session.Shutdown = Task.Run(() => ShutdownAsync(session, askToQuit, grace));
        _shutdowns = _shutdowns.IsCompleted ? shutdown : Task.WhenAll(_shutdowns, shutdown);
    }

    private async Task ShutdownAsync(Session session, bool askToQuit, TimeSpan grace)
    {
        try
        {
            // The launch observes the cancellation at its next step. Waiting for it means the
            // session's processes are final: none can appear after they have been dealt with.
            try
            {
                await session.Launch.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reported to whoever awaited the start.
            }

            RdpClient? client;
            List<Process> processes;
            string? profile;
            lock (_gate)
            {
                client = session.Client;
                processes = [.. Processes(session)];
                profile = session.ProfileDirectory;
            }

            var clock = Stopwatch.StartNew();
            if (client is not null)
            {
                if (askToQuit && !client.IsClosed && grace > TimeSpan.Zero)
                {
                    try
                    {
                        // An orderly quit flushes the profile. The connection usually drops
                        // before the reply arrives, which is as good as the reply.
                        await client.EvaluateAsync(AgentCall("quit")).WaitAsync(grace).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is RdpException or TimeoutException)
                    {
                    }
                }
                client.Close();
            }

            var remaining = grace - clock.Elapsed;
            if (!await EngineRecord.WaitForExitAsync(processes, remaining).ConfigureAwait(false))
            {
                foreach (var process in processes) EngineRecord.Kill(process);
                await EngineRecord.WaitForExitAsync(processes, KillSettle).ConfigureAwait(false);
            }

            // On Windows the launched process is usually Firefox's launcher. When it goes, the
            // operating system ends the browser process it started (the launcher keeps it in a
            // kill-on-close job), but a moment later, and that browser is not among `processes`
            // if the launch was stopped before the agent reported it. Its profile lock being
            // released is the sign that it has gone too; without this wait, a start that
            // immediately follows a stop finds the profile still in use.
            if (profile is not null) await WaitForProfileReleaseAsync(profile, KillSettle).ConfigureAwait(false);

            if (profile is not null && processes.All(EngineRecord.HasExited)) EngineRecord.Delete(profile, session.Token);
            foreach (var process in processes) process.Dispose();
        }
        catch (Exception)
        {
            // Shutting down is best effort and must complete: StopAndWait and the next launch
            // both wait on this task. Whatever was missed is caught by the leftover-engine check.
        }
        finally
        {
            lock (_gate) _closing.Remove(session);
        }
    }

    /// <summary>
    /// The launch's own processes: the one Mosaic created and Gecko's browser process. Not their
    /// descendants; see <see cref="EngineRecord.Processes"/>.
    /// </summary>
    private static IEnumerable<Process> Processes(Session session)
    {
        if (session.Launched is { } launched) yield return launched;
        if (session.Browser is { } browser) yield return browser;
    }

    // ---- Agent calls ----------------------------------------------------------------------

    public async Task<JsonNode?> CallAsync(string method, params object?[] arguments)
    {
        var script = AgentScript(method, arguments);
        RdpClient client;
        lock (_gate)
        {
            if (_status != GeckoStatus.Running || _session?.Client is not { } current)
                throw new InvalidOperationException("Gecko is not running.");
            client = current;
        }
        return UnwrapAgentResult(await client.EvaluateAsync(script).ConfigureAwait(false));
    }

    public void Send(string method, params object?[] arguments) =>
        CallAsync(method, arguments).ContinueWith(
            static call => _ = call.Exception, // observed, so it is not reported as unobserved later
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task PollAsync(Session session, RdpClient client)
    {
        var cancellation = session.Cancellation.Token;
        while (!cancellation.IsCancellationRequested && !client.IsClosed)
        {
            try
            {
                var state = await client.EvaluateAsync(AgentCall("state")).ConfigureAwait(false);
                if (state is JsonValue text && text.TryGetValue(out string? json) &&
                    ParseSnapshot(json) is { Ready: true } snapshot)
                {
                    Raise(() =>
                    {
                        // Checked on delivery: a snapshot queued behind a Stop() describes a
                        // Gecko the listener has already been told is gone.
                        bool current;
                        lock (_gate) current = _session == session;
                        if (current) SnapshotReceived?.Invoke(snapshot);
                    });
                }
            }
            catch (RdpException)
            {
                // A failed poll is skipped; a closed connection ends the loop.
            }

            try
            {
                await Task.Delay(_options.PollInterval, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Raise(Action action)
    {
        if (_context is null) action();
        else _context.Post(static state => ((Action)state!)(), action);
    }

    /// <summary>Registers <c>resource://mosaic/</c> → the agent folder and loads the agent.</summary>
    internal static string BootstrapScript(string agentDirectory, string token) => $$"""
        (() => {
          if (Services.env.get({{JsLiteral(TokenVariable)}}) !== {{JsLiteral(token)}}) {
            throw new Error({{JsLiteral(WrongEngineMarker)}});
          }
          const Ci = Components.interfaces, Cc = Components.classes;
          const resource = Services.io.getProtocolHandler("resource")
            .QueryInterface(Ci.nsISubstitutingProtocolHandler);
          const directory = Cc["@mozilla.org/file/local;1"].createInstance(Ci.nsIFile);
          directory.initWithPath({{JsLiteral(agentDirectory)}});
          resource.setSubstitution("mosaic", Services.io.newFileURI(directory));
          return {{AgentExpression}}.version();
        })()
        """;

    /// <summary>
    /// <see cref="AgentCall"/> wrapped so that whatever happens comes back as one JSON string:
    /// <c>{"ok":true,"value":&lt;the method's JSON&gt;}</c> or <c>{"ok":false,"error":"…"}</c>.
    /// The debugging protocol reports a rejected promise with no reason at all, so without the
    /// wrapper a failing async method (installExtension, for one) would lose its error message.
    /// </summary>
    internal static string AgentScript(string method, params object?[] arguments) => $$"""
        (async () => {
          try {
            return JSON.stringify({ ok: true, value: await {{AgentCall(method, arguments)}} });
          } catch (error) {
            return JSON.stringify({ ok: false, error: String(error?.message ?? error) });
          }
        })()
        """;

    /// <summary>
    /// Unwraps <see cref="AgentScript"/>'s envelope into the method's decoded result. A reply that
    /// is not an envelope decodes to null, like any other unreadable result.
    /// </summary>
    /// <exception cref="RdpException">The agent method failed; the message is the agent's.</exception>
    internal static JsonNode? UnwrapAgentResult(JsonNode? result)
    {
        if (DecodeResult(result) is not JsonObject envelope ||
            envelope["ok"] is not JsonValue okValue || !okValue.TryGetValue(out bool ok))
            return null;
        if (!ok)
        {
            var message = envelope["error"] is JsonValue error && error.TryGetValue(out string? text) ? text : null;
            throw new RdpException(RdpErrorKind.Remote, message ?? "the Gecko agent reported an error");
        }
        return envelope["value"] is JsonValue value ? DecodeResult(value) : null;
    }

    internal static string AgentCall(string method, params object?[] arguments)
    {
        // The name is spliced into script, so it has to be a plain identifier and nothing more.
        var isIdentifier = method.Length > 0 && !char.IsAsciiDigit(method[0]) &&
            method.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        if (!isIdentifier) throw new ArgumentException($"'{method}' is not an agent method name.", nameof(method));
        return $"{AgentExpression}.{method}({string.Join(", ", arguments.Select(JsLiteral))})";
    }

    /// <summary>
    /// JSON is a subset of JavaScript, so a JSON-encoded value is a safe literal.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not null, a string, a boolean, a number or a <see cref="JsonNode"/>.</exception>
    internal static string JsLiteral(object? value)
    {
        JsonNode? node = value switch
        {
            null => null,
            JsonNode json => json,
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            uint number => JsonValue.Create(number),
            ulong number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            ushort number => JsonValue.Create(number),
            byte number => JsonValue.Create(number),
            sbyte number => JsonValue.Create(number),
            _ => throw new ArgumentException(
                $"Agent arguments must be null, strings, booleans, numbers or JSON nodes, not {value.GetType().Name}.", nameof(value)),
        };
        return node?.ToJsonString() ?? "null";
    }

    /// <summary>Agent methods return JSON text; this decodes it. Anything else decodes to null.</summary>
    internal static JsonNode? DecodeResult(JsonNode? result)
    {
        if (result is not JsonValue value || !value.TryGetValue(out string? text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTrue(JsonNode? result) =>
        result is JsonValue value && value.TryGetValue(out string? text) && text == "true";

    /// <summary>
    /// Reads <c>pid</c> and <c>windowHandle</c> from the agent's <c>version()</c>. The handle is a
    /// decimal string (a pointer does not fit a JSON number reliably); a number, or Gecko's own
    /// <c>0x…</c> spelling, is accepted too. No window, as in headless mode, is zero.
    /// </summary>
    internal static (int ProcessId, nint WindowHandle) ParseVersion(JsonNode? version)
    {
        if (version is not JsonObject description ||
            description["pid"] is not JsonValue pid || !pid.TryGetValue(out int processId) || processId <= 0)
        {
            throw new GeckoEngineException(GeckoFailure.AgentOutOfDate, "Gecko's agent did not report its process.");
        }

        nint handle = 0;
        if (description["windowHandle"] is JsonValue reported)
        {
            if (reported.TryGetValue(out long number))
            {
                handle = (nint)number;
            }
            else if (reported.TryGetValue(out string? text))
            {
                var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                _ = hex
                    ? nint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out handle)
                    : nint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out handle);
            }
        }
        return (processId, handle);
    }

    /// <summary>Decodes the agent's <c>state()</c>; null if it is not what that method returns.</summary>
    internal static GeckoSnapshot? ParseSnapshot(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tabs = root.GetProperty("tabs").EnumerateArray().Select(tab => new GeckoTabState(
                tab.GetProperty("id").GetInt32(),
                tab.GetProperty("url").GetString() ?? "",
                tab.GetProperty("title").GetString() ?? "",
                tab.GetProperty("loading").GetBoolean(),
                tab.GetProperty("canGoBack").GetBoolean(),
                tab.GetProperty("canGoForward").GetBoolean(),
                tab.GetProperty("selected").GetBoolean(),
                // Absent from an agent that has no content blocker loaded yet.
                tab.TryGetProperty("blocked", out var blocked) ? blocked.GetInt32() : 0)).ToList();
            var events = root.GetProperty("events").EnumerateArray()
                .Select(entry => entry.GetProperty("type").GetString() ?? "").ToList();
            return new GeckoSnapshot(root.GetProperty("ready").GetBoolean(), tabs, events);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
