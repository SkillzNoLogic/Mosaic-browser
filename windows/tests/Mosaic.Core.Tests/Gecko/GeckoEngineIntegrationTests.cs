using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Tests.Gecko;

/// <summary>
/// A fact that drives a real Gecko. Runs only when <c>MOSAIC_GECKO_EXE</c> names a Firefox
/// executable, so a machine without one still passes.
/// </summary>
public sealed class GeckoFactAttribute : FactAttribute
{
    public GeckoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GeckoRuntime.OverrideVariable)))
        {
            Skip = $"Set {GeckoRuntime.OverrideVariable} to a Firefox executable to run the Gecko integration tests.";
        }
    }
}

/// <summary>
/// <see cref="GeckoEngine"/> end to end against the Firefox named by <c>MOSAIC_GECKO_EXE</c>,
/// over the loopback TCP transport Windows uses. Gecko runs headless, on a throwaway profile,
/// and every test ends whatever it started even when it fails.
/// </summary>
public sealed class GeckoEngineIntegrationTests : IDisposable
{
    private readonly TemporaryDirectory _profile = new();
    private readonly List<GeckoEngine> _engines = [];
    private readonly List<int> _launched = [];

    static GeckoEngineIntegrationTests()
    {
        // Gecko's own switch for keeping a headless instance out of the macOS Dock; inherited by
        // the engines started here. It means nothing on other systems.
        Environment.SetEnvironmentVariable("MOZ_APP_NO_DOCK", "1");
    }

    private static string AgentDirectory
    {
        get
        {
            // The agent is not copied beside the tests; find it in the source tree.
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, "resources", "gecko");
                if (File.Exists(Path.Combine(candidate, "agent.sys.mjs"))) return candidate;
            }
            throw new DirectoryNotFoundException("resources/gecko/agent.sys.mjs was not found above the test folder.");
        }
    }

    private GeckoEngine NewEngine(TimeSpan? handOffGrace = null)
    {
        var runtime = GeckoRuntime.Locate();
        Assert.NotNull(runtime);
        Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable(GeckoRuntime.OverrideVariable)!), runtime.ExecutablePath);

        var engine = new GeckoEngine(new GeckoEngineOptions
        {
            ProfileDirectory = _profile.File("profile"),
            AgentDirectory = AgentDirectory,
            Runtime = runtime,
            ExtraArguments = ["--headless"],
            PollInterval = TimeSpan.FromMilliseconds(50),
            ProcessLaunched = id => { lock (_launched) _launched.Add(id); },
            HandOffGrace = handOffGrace ?? new GeckoEngineOptions().HandOffGrace,
        });
        _engines.Add(engine);
        return engine;
    }

    public void Dispose()
    {
        foreach (var engine in _engines) engine.StopAndWait(TimeSpan.FromSeconds(5));
        lock (_launched)
        {
            foreach (var id in _launched) Assert.False(IsRunning(id), $"Gecko process {id} was left running");
        }
        _profile.Dispose();
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Collects snapshots so a test can wait for Gecko to reach a state.</summary>
    private sealed class Watcher
    {
        private readonly List<GeckoSnapshot> _snapshots = [];
        private readonly List<string> _events = [];

        public Watcher(GeckoEngine engine) => engine.SnapshotReceived += snapshot =>
        {
            lock (_snapshots)
            {
                _snapshots.Add(snapshot);
                _events.AddRange(snapshot.Events);
            }
        };

        public List<string> Events
        {
            get { lock (_snapshots) return [.. _events]; }
        }

        public GeckoSnapshot? Latest
        {
            get { lock (_snapshots) return _snapshots.LastOrDefault(); }
        }

        public async Task<GeckoSnapshot> UntilAsync(Func<GeckoSnapshot, bool> reached, string what)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (Latest is { } latest && reached(latest)) return latest;
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30),
                    $"Gecko never reported {what}; last tabs: {string.Join(" | ", Latest?.Tabs ?? [])}");
                await Task.Delay(25);
            }
        }
    }

    [GeckoFact]
    public async Task StartsLoadsTheAgentDrivesTabsAndStopsLeavingNothingBehind()
    {
        var engine = NewEngine();
        var watcher = new Watcher(engine);
        var started = 0;
        var stopped = 0;
        engine.OnStarted = () =>
        {
            Interlocked.Increment(ref started);
            return Task.CompletedTask;
        };
        engine.Stopped += () => Interlocked.Increment(ref stopped);
        Assert.Equal(GeckoStatus.Stopped, engine.Status);

        var start = engine.StartAsync();
        Assert.Same(start, engine.StartAsync()); // concurrent callers share one launch
        await start;

        Assert.Equal(GeckoStatus.Running, engine.Status);
        Assert.Equal(1, Volatile.Read(ref started));
        Assert.Same(Task.CompletedTask, engine.StartAsync());

        // version(): the agent is loaded and reports the browser process and its window.
        var version = Assert.IsType<JsonObject>(await engine.CallAsync("version"));
        Assert.Equal(2, version["agent"]!.GetValue<int>());
        Assert.Equal(engine.Runtime!.Name, version["app"]!.GetValue<string>());
        Assert.Equal(engine.Runtime.Version, version["appVersion"]!.GetValue<string>());
        var processId = version["pid"]!.GetValue<int>();
        Assert.Equal(processId, engine.ProcessId);
        Assert.True(IsRunning(processId));
        var windowHandle = version["windowHandle"]!.GetValue<string>();
        Assert.Equal((nint)long.Parse(windowHandle, CultureInfo.InvariantCulture), engine.WindowHandle);
        // A headless Gecko has no native window, and these tests only ever run it headless.
        Assert.Equal("0", windowHandle);

        // On Windows the launched process may be Firefox's launcher; either way both are on record.
        var record = EngineRecord.Load(_profile.File("profile"))!;
        Assert.Equal(ProcessIdentity.Current, record.Owner);
        Assert.Equal(_launched.Single(), record.Launched.Id);
        Assert.Contains(processId, record.Processes.Select(identity => identity.Id));

        // setup() succeeded as part of starting; it stays true and the chrome is gone.
        Assert.True((await engine.CallAsync("setup"))!.GetValue<bool>());
        var layout = (await engine.CallAsync("rect"))!;
        Assert.Equal("true", layout["hidechrome"]!.GetValue<string>());
        Assert.Equal(layout["height"]!.GetValue<int>(), layout["innerHeight"]!.GetValue<int>());

        // Gecko's own startup tab is not reported.
        Assert.Empty((await watcher.UntilAsync(_ => true, "anything")).Tabs);

        var first = (await engine.CallAsync("openTab", "data:text/html,<title>Mosaic one</title>one", true))!.GetValue<int>();
        var shown = await watcher.UntilAsync(
            snapshot => snapshot.Tabs.Any(tab => tab.Id == first && tab.Title == "Mosaic one" && !tab.Loading), "the first page");
        var tab = shown.Tabs.Single();
        Assert.StartsWith("data:text/html,", tab.Url);
        Assert.True(tab.Selected);
        Assert.False(tab.CanGoBack);

        Assert.True((await engine.CallAsync("navigate", first, "data:text/html,<title>Mosaic two</title>two"))!.GetValue<bool>());
        await watcher.UntilAsync(
            snapshot => snapshot.Tabs.Any(tab => tab.Id == first && tab.Title == "Mosaic two" && tab.CanGoBack), "the second page");

        engine.Send("goBack", first);
        await watcher.UntilAsync(
            snapshot => snapshot.Tabs.Any(tab => tab.Id == first && tab.Title == "Mosaic one" && tab.CanGoForward), "going back");

        var second = (await engine.CallAsync("openTab", "about:blank", false))!.GetValue<int>();
        Assert.NotEqual(first, second);
        await watcher.UntilAsync(snapshot => snapshot.Tabs.Count == 2 && snapshot.Tabs.Single(tab => tab.Selected).Id == first, "two tabs");
        await engine.CallAsync("selectTab", second);
        await watcher.UntilAsync(snapshot => snapshot.Tabs.Count == 2 && snapshot.Tabs.Single(tab => tab.Selected).Id == second, "the selection");

        // A method that throws inside Gecko surfaces as a remote error and does no harm.
        var failure = await Assert.ThrowsAsync<RdpException>(() => engine.CallAsync("selectTab", 9999));
        Assert.Equal(RdpErrorKind.Remote, failure.Kind);
        Assert.Contains("no tab 9999", failure.Message);

        Assert.True((await engine.CallAsync("closeTab", first))!.GetValue<bool>());
        Assert.True((await engine.CallAsync("closeTab", second))!.GetValue<bool>());
        await watcher.UntilAsync(snapshot => snapshot.Tabs.Count == 0, "no tabs");
        Assert.Empty(Assert.IsType<JsonArray>(await engine.CallAsync("listExtensions")));

        engine.StopAndWait(TimeSpan.FromSeconds(10));

        Assert.Equal(GeckoStatus.Stopped, engine.Status);
        Assert.Null(engine.ProcessId);
        Assert.False(IsRunning(processId));
        Assert.False(IsRunning(_launched.Single()));
        Assert.False(File.Exists(Path.Combine(_profile.File("profile"), EngineRecord.FileName)));
        await Eventually.TrueAsync(() => Volatile.Read(ref stopped) == 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CallAsync("version"));
    }

    [GeckoFact]
    public async Task ShortcutsPressedInsideGeckoReachMosaicAsEvents()
    {
        var engine = NewEngine();
        var watcher = new Watcher(engine);
        await engine.StartAsync();

        // A second debugging connection plays the user: it sends key presses to Gecko's window.
        using var keyboard = await RdpClient.ConnectTcpAsync("127.0.0.1", engine.DebuggerPort!.Value);
        await keyboard.AttachToParentProcessAsync();
        var pressed = JsonNode.Parse((await keyboard.EvaluateAsync("""
            (() => {
              const win = Services.wm.getMostRecentWindow("navigator:browser");
              const mac = Services.appinfo.OS === "Darwin";
              const root = win.document.documentElement;
              // True when the agent's window-level hook kept the key press from travelling on.
              const press = (key, { shortcut = false, shift = false, alt = false } = {}) => {
                let arrived = false;
                const listener = () => { arrived = true; };
                root.addEventListener("keydown", listener, true);
                root.dispatchEvent(new win.KeyboardEvent("keydown", {
                  key, bubbles: true, cancelable: true,
                  metaKey: shortcut && mac, ctrlKey: shortcut && !mac, shiftKey: shift, altKey: alt,
                }));
                root.removeEventListener("keydown", listener, true);
                return !arrived;
              };
              const before = win.gBrowser.tabs.length;
              const intercepted = {
                l: press("l", { shortcut: true }),
                t: press("t", { shortcut: true }),
                n: press("n", { shortcut: true }),
                w: press("w", { shortcut: true }),
                E: press("E", { shortcut: true, shift: true }),
                closeWindow: press("W", { shortcut: true, shift: true }),
                privateWindow: press("P", { shortcut: true, shift: true }),
                plainL: press("l"),
                altL: press("l", { shortcut: true, alt: true }),
                otherShortcut: press("8", { shortcut: true }),
              };
              return JSON.stringify({ intercepted, tabsOpened: win.gBrowser.tabs.length - before,
                                      windows: [...Services.wm.getEnumerator("navigator:browser")].length });
            })()
            """))!.GetValue<string>())!;

        var intercepted = pressed["intercepted"]!.AsObject().ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<bool>());
        // Mosaic's shortcuts, and Gecko's whole-window ones, never reach Gecko's own handling.
        Assert.All(["l", "t", "n", "w", "E", "closeWindow", "privateWindow"], key => Assert.True(intercepted[key], key));
        // Everything else is left to Gecko and the page.
        Assert.All(["plainL", "altL", "otherShortcut"], key => Assert.False(intercepted[key], key));
        Assert.Equal(0, pressed["tabsOpened"]!.GetValue<int>());
        Assert.Equal(1, pressed["windows"]!.GetValue<int>());

        await Eventually.TrueAsync(() => watcher.Events.Count >= 5, because: "the shortcut events never arrived");
        Assert.Equal(["focusAddress", "newTab", "newTab", "closeTab", "toggleEngine"], watcher.Events);
    }

    [GeckoFact]
    public async Task AnEngineLeftBehindIsTerminatedSoTheNextOneCanStart()
    {
        // The first engine stands for one whose Mosaic went away without stopping it: it is
        // never stopped here, it just loses its profile to the second.
        var abandoned = NewEngine();
        var abandonedStopped = 0;
        abandoned.Stopped += () => Interlocked.Increment(ref abandonedStopped);
        await abandoned.StartAsync();
        var oldProcess = abandoned.ProcessId!.Value;

        var engine = NewEngine();
        await engine.StartAsync();

        Assert.Equal(GeckoStatus.Running, engine.Status);
        Assert.NotEqual(oldProcess, engine.ProcessId);
        Assert.False(IsRunning(oldProcess));
        // The new Gecko is the one this engine launched, on a profile it has to itself.
        Assert.Equal(engine.ProcessId, (await engine.CallAsync("version"))!["pid"]!.GetValue<int>());
        var id = (await engine.CallAsync("openTab", "about:blank", true))!.GetValue<int>();
        Assert.True(id > 0);

        // And the engine that lost its Gecko noticed.
        await Eventually.TrueAsync(() => abandoned.Status == GeckoStatus.Stopped && Volatile.Read(ref abandonedStopped) == 1);
    }

    [GeckoFact]
    public async Task AGeckoThereIsNoRecordOfIsNeitherKilledNorTakenOver()
    {
        var holder = NewEngine();
        await holder.StartAsync();
        var holderProcess = holder.ProcessId!.Value;
        // Without the record, the second engine has no proof the Gecko on this profile is Mosaic's.
        File.Delete(Path.Combine(_profile.File("profile"), EngineRecord.FileName));

        var engine = NewEngine(handOffGrace: TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<GeckoEngineException>(engine.StartAsync);

        // On Windows the held profile lock is seen before anything is launched. Elsewhere the new
        // Gecko finds the profile taken and exits "successfully": either after giving up (what
        // Firefox ESR 153 does on macOS), or after handing its command line to the holder, which
        // is then turned away for not carrying this launch's token.
        GeckoFailure[] expected = OperatingSystem.IsWindows()
            ? [GeckoFailure.ProfileInUse]
            : [GeckoFailure.ExitedDuringStartup, GeckoFailure.WrongEngine];
        Assert.Contains(error.Failure, expected);
        Assert.Equal(GeckoStatus.Failed, engine.Status);
        Assert.True(IsRunning(holderProcess));
        Assert.Equal(GeckoStatus.Running, holder.Status);
        Assert.Equal(holderProcess, (await holder.CallAsync("version"))!["pid"]!.GetValue<int>());
    }

    [GeckoFact]
    public async Task StoppingWhileGeckoIsStillStartingLeavesNoProcessBehind()
    {
        var engine = NewEngine();

        var start = engine.StartAsync();
        await Eventually.TrueAsync(() => { lock (_launched) return _launched.Count == 1; }, seconds: 30);
        await Task.Delay(300); // into Gecko's startup, long before it can have finished
        Assert.Equal(GeckoStatus.Starting, engine.Status);
        engine.StopAndWait(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(GeckoStatus.Stopped, engine.Status);
        Assert.False(IsRunning(_launched.Single()));

        // The profile is free again: a fresh start works, and quitting Gecko from inside
        // (as its own Quit command would) is noticed.
        await engine.StartAsync();
        var stopped = 0;
        engine.Stopped += () => Interlocked.Increment(ref stopped);
        var processId = engine.ProcessId!.Value;
        engine.Send("quit");
        await Eventually.TrueAsync(() => engine.Status == GeckoStatus.Stopped && Volatile.Read(ref stopped) == 1);
        await Eventually.TrueAsync(() => !IsRunning(processId));
    }
}
