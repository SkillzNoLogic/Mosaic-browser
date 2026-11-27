using System.Diagnostics;
using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Tests.Gecko;

/// <summary>
/// <see cref="GeckoEngine"/> without a real Gecko: its pure helpers, and its lifecycle driven
/// against a shell script standing in for <c>firefox.exe</c> plus <see cref="FakeGecko"/> standing
/// in for the browser's debugging server. That pairing is how the Windows launcher-process
/// behaviour (the launched process is not the browser) is exercised on any machine.
/// </summary>
public class GeckoEngineTests
{
    // ---- Helpers --------------------------------------------------------------------------

    [Fact]
    public void AgentScriptsWrapTheCallInAnEnvelope()
    {
        var script = GeckoEngine.AgentScript("openTab", "about:blank", true);
        Assert.Contains($"await {GeckoEngine.AgentCall("openTab", "about:blank", true)}", script);
        Assert.Contains("ok: false", script);
    }

    [Fact]
    public void AgentResultsAreUnwrappedAndFailuresCarryTheAgentsMessage()
    {
        Assert.Equal(7, GeckoEngine.UnwrapAgentResult(JsonValue.Create("""{"ok":true,"value":"7"}"""))!.GetValue<int>());
        Assert.Null(GeckoEngine.UnwrapAgentResult(JsonValue.Create("""{"ok":true,"value":"null"}""")));
        Assert.Null(GeckoEngine.UnwrapAgentResult(JsonValue.Create("""{"ok":true}""")));
        Assert.Null(GeckoEngine.UnwrapAgentResult(JsonValue.Create("not an envelope")));
        Assert.Null(GeckoEngine.UnwrapAgentResult(null));

        var failure = Assert.Throws<RdpException>(() =>
            GeckoEngine.UnwrapAgentResult(JsonValue.Create("""{"ok":false,"error":"no tab 9"}""")));
        Assert.Equal(RdpErrorKind.Remote, failure.Kind);
        Assert.Equal("no tab 9", failure.Message);
    }

    [Fact]
    public void AgentCallsEncodeArgumentsAsJavaScriptLiterals()
    {
        const string agent = """ChromeUtils.importESModule("resource://mosaic/agent.sys.mjs").Agent""";

        Assert.Equal($"{agent}.setup()", GeckoEngine.AgentCall("setup"));
        Assert.Equal($"{agent}.openTab(\"https://example.com/?q=1\", true)",
            GeckoEngine.AgentCall("openTab", "https://example.com/?q=1", true));
        Assert.Equal($"{agent}.navigate(7, null)", GeckoEngine.AgentCall("navigate", 7, null));
        Assert.Equal($"{agent}.setRect(-5, 0, 1.5, 9000000000)", GeckoEngine.AgentCall("setRect", -5, 0L, 1.5, 9_000_000_000L));
        Assert.Equal($"{agent}.x({{\"a\":[1,\"b\"]}})",
            GeckoEngine.AgentCall("x", new JsonObject { ["a"] = new JsonArray(1, "b") }));
    }

    [Fact]
    public void StringArgumentsCannotBreakOutOfTheirLiteral()
    {
        var hostile = "\"); Services.startup.quit(0); (\"\\ \n \u2028 </script> 'x' é";

        var literal = GeckoEngine.JsLiteral(hostile);

        // Still exactly one JSON string, which decodes back to the input.
        Assert.Equal(hostile, JsonNode.Parse(literal)!.GetValue<string>());
        Assert.DoesNotContain('\n', literal);
        Assert.DoesNotContain('\u2028', literal);
        Assert.DoesNotContain("\");", literal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("version(); evil")]
    [InlineData("a.b")]
    [InlineData("1abc")]
    [InlineData("naïve")]
    public void AgentMethodNamesMustBeIdentifiers(string method) =>
        Assert.Throws<ArgumentException>(() => GeckoEngine.AgentCall(method));

    [Fact]
    public void UnsupportedArgumentTypesAreRejected() =>
        Assert.Throws<ArgumentException>(() => GeckoEngine.AgentCall("x", new object()));

    [Fact]
    public void TheBootstrapScriptChecksTheLaunchTokenBeforeAnythingElse()
    {
        var script = GeckoEngine.BootstrapScript(@"C:\Program Files\Mosaic\gecko", "abc123");

        Assert.Contains("""Services.env.get("MOSAIC_ENGINE_TOKEN") !== "abc123" """.Trim(), script);
        Assert.Contains("""initWithPath("C:\\Program Files\\Mosaic\\gecko")""", script);
        Assert.True(script.IndexOf("MOSAIC_ENGINE_TOKEN", StringComparison.Ordinal) <
                    script.IndexOf("setSubstitution", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"pid":4242,"windowHandle":"66052"}""", 4242, 66052L)]
    [InlineData("""{"pid":4242,"windowHandle":"0"}""", 4242, 0L)]
    [InlineData("""{"pid":4242,"windowHandle":"140735340871696"}""", 4242, 140735340871696L)]
    [InlineData("""{"pid":4242,"windowHandle":66052}""", 4242, 66052L)]
    [InlineData("""{"pid":4242,"windowHandle":"0x00010204"}""", 4242, 66052L)]
    [InlineData("""{"pid":4242,"windowHandle":"nonsense"}""", 4242, 0L)]
    [InlineData("""{"pid":4242}""", 4242, 0L)]
    public void ReadsProcessAndWindowFromTheAgentsVersion(string json, int processId, long windowHandle) =>
        Assert.Equal((processId, (nint)windowHandle), GeckoEngine.ParseVersion(JsonNode.Parse(json)));

    [Theory]
    [InlineData("""{"agent":1,"gecko":"153.0"}""")]
    [InlineData("""{"pid":"12"}""")]
    [InlineData("""{"pid":0}""")]
    [InlineData("null")]
    public void AVersionWithoutAProcessIsAnOutdatedAgent(string json) =>
        Assert.Equal(GeckoFailure.AgentOutOfDate,
            Assert.Throws<GeckoEngineException>(() => GeckoEngine.ParseVersion(JsonNode.Parse(json))).Failure);

    [Fact]
    public void DecodesSnapshots()
    {
        var snapshot = GeckoEngine.ParseSnapshot("""
            {"ready":true,
             "tabs":[{"id":3,"url":"https://example.com/","title":"Example","loading":true,
                      "canGoBack":true,"canGoForward":false,"selected":true,"blocked":4}],
             "events":[{"type":"newTab"},{"type":"focusAddress"}]}
            """);

        Assert.True(snapshot!.Ready);
        Assert.Equal([new GeckoTabState(3, "https://example.com/", "Example", true, true, false, true)], snapshot.Tabs);
        Assert.Equal(["newTab", "focusAddress"], snapshot.Events);
        Assert.Null(GeckoEngine.ParseSnapshot("true"));
        Assert.Null(GeckoEngine.ParseSnapshot("""{"ready":true,"tabs":[{"id":"x"}],"events":[]}"""));
        Assert.Null(GeckoEngine.ParseSnapshot("not json"));
    }

    [Fact]
    public void TheListenerTableSeesAListeningPort()
    {
        using var server = new FakeGecko();
        // null would mean this system cannot say, which is tolerated but worth knowing about.
        Assert.True(GeckoEngine.IsListening(server.Port));
    }

    // ---- Lifecycle without a runtime ------------------------------------------------------

    [Fact]
    public async Task AStoppedEngineRefusesCallsAndStopsHarmlessly()
    {
        using var profile = new TemporaryDirectory();
        var engine = new GeckoEngine(new GeckoEngineOptions { ProfileDirectory = profile.Path });

        Assert.Equal(GeckoStatus.Stopped, engine.Status);
        Assert.Null(engine.ProcessId);
        Assert.Equal(0, engine.WindowHandle);
        Assert.Null(engine.FailureMessage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CallAsync("version"));
        engine.Send("version");
        engine.Stop();
        engine.StopAndWait(TimeSpan.FromSeconds(1));
        Assert.Equal(GeckoStatus.Stopped, engine.Status);
    }

    [Fact]
    public async Task StartingWithoutAnAgentFailsWithAPresentableMessage()
    {
        using var folder = new TemporaryDirectory();
        var statuses = new List<GeckoStatus>();
        var engine = new GeckoEngine(new GeckoEngineOptions
        {
            ProfileDirectory = folder.File("profile"),
            AgentDirectory = folder.File("no-agent-here"),
            Runtime = new GeckoRuntime(folder.File("firefox"), "Fake", "1", false),
        });
        engine.StatusChanged += () => { lock (statuses) statuses.Add(engine.Status); };

        var error = await Assert.ThrowsAsync<GeckoEngineException>(engine.StartAsync);

        Assert.Equal(GeckoFailure.AgentMissing, error.Failure);
        Assert.Equal(GeckoStatus.Failed, engine.Status);
        Assert.Equal(error.Message, engine.FailureMessage);
        await Eventually.TrueAsync(() => { lock (statuses) return statuses.Contains(GeckoStatus.Failed); });
        // Nothing was created for a start that never got as far as launching.
        Assert.False(Directory.Exists(folder.File("profile")));

        engine.Stop();
        Assert.Equal(GeckoStatus.Stopped, engine.Status);
        Assert.Null(engine.FailureMessage);
    }

    [Fact]
    public async Task AnExecutableThatCannotBeRunIsALaunchFailure()
    {
        using var folder = new TemporaryDirectory();
        File.WriteAllText(folder.File("agent.sys.mjs"), "");
        var engine = new GeckoEngine(new GeckoEngineOptions
        {
            ProfileDirectory = folder.File("profile"),
            AgentDirectory = folder.Path,
            Runtime = new GeckoRuntime(folder.File("no-such-firefox"), "Fake", "1", false),
        });

        var error = await Assert.ThrowsAsync<GeckoEngineException>(engine.StartAsync);

        Assert.Equal(GeckoFailure.LaunchFailed, error.Failure);
        Assert.Equal(GeckoStatus.Failed, engine.Status);
    }

    [Fact]
    public async Task AProfileHeldByAnUnknownGeckoIsReportedNotFoughtOver()
    {
        using var folder = new TemporaryDirectory();
        File.WriteAllText(folder.File("agent.sys.mjs"), "");
        var profile = Directory.CreateDirectory(folder.File("profile")).FullName;
        var launched = false;
        var engine = new GeckoEngine(new GeckoEngineOptions
        {
            ProfileDirectory = profile,
            AgentDirectory = folder.Path,
            Runtime = new GeckoRuntime(folder.File("no-such-firefox"), "Fake", "1", false),
            ProcessLaunched = _ => launched = true,
        });

        // What a running Gecko looks like on Windows, with no record of Mosaic having launched it.
        using (new FileStream(Path.Combine(profile, "parent.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<GeckoEngineException>(engine.StartAsync);
            Assert.Equal(GeckoFailure.ProfileInUse, error.Failure);
            Assert.False(launched);
        }
    }

    // ---- Lifecycle against a stand-in launcher and browser --------------------------------

    /// <summary>
    /// A fake Gecko installation: <c>firefox</c> is a shell script that notes the debugging port
    /// it was given and then behaves as <paramref name="then"/> says; the browser's side of the
    /// debugging protocol is played by a <see cref="FakeGecko"/> the test starts on that port.
    /// </summary>
    private sealed class Stage : IDisposable
    {
        private readonly TemporaryDirectory _folder = new();
        private readonly List<Process> _processes = [];

        public Stage(string then)
        {
            File.WriteAllText(_folder.File("agent.sys.mjs"), "");
            File.WriteAllText(_folder.File("firefox"), $"""
                #!/bin/sh
                printf '%s\n' "$@" > '{_folder.File("arguments.tmp")}'
                printf '%s' "$MOSAIC_ENGINE_TOKEN" > '{_folder.File("token")}'
                mv '{_folder.File("arguments.tmp")}' '{_folder.File("arguments")}'
                {then}

                """.ReplaceLineEndings("\n"));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_folder.File("firefox"),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string Profile => _folder.File("profile");
        public List<int> Launched { get; } = [];

        public GeckoEngineOptions Options(TimeSpan? handOffGrace = null) => new()
        {
            ProfileDirectory = Profile,
            AgentDirectory = _folder.Path,
            Runtime = new GeckoRuntime(_folder.File("firefox"), "Fake", "1", false),
            PollInterval = TimeSpan.FromMilliseconds(20),
            ProcessLaunched = id => { lock (Launched) Launched.Add(id); },
            StartupTimeout = TimeSpan.FromSeconds(20),
            HandOffGrace = handOffGrace ?? TimeSpan.FromSeconds(15),
        };

        /// <summary>The command line the stand-in was started with, once it has run.</summary>
        public async Task<string[]> ArgumentsAsync()
        {
            await Eventually.TrueAsync(() => File.Exists(_folder.File("arguments")), because: "the stand-in was never started");
            return File.ReadAllLines(_folder.File("arguments"));
        }

        public string Token => File.ReadAllText(_folder.File("token"));

        /// <summary>Starts the browser's debugging server on the port the engine chose.</summary>
        public async Task<FakeGecko> BrowserAsync(Process browser, Func<string, JsonNode?>? responder = null, string windowHandle = "66052")
        {
            var arguments = await ArgumentsAsync();
            var port = int.Parse(arguments[Array.IndexOf(arguments, "--start-debugger-server") + 1]);
            var version = new JsonObject { ["agent"] = 2, ["pid"] = browser.Id, ["windowHandle"] = windowHandle }.ToJsonString();
            return new FakeGecko(FakeTransport.Tcp, port)
            {
                HangUpOn = ".quit(",
                Responder = text => responder?.Invoke(text) ?? text switch
                {
                    _ when text.Contains("setSubstitution") || text.Contains(".version(") => version,
                    _ when text.Contains(".setup(") => "true",
                    _ when text.Contains(".state(") =>
                        """{"ready":true,"tabs":[{"id":1,"url":"about:blank","title":"Blank","loading":false,"canGoBack":false,"canGoForward":false,"selected":true}],"events":[{"type":"newTab"}]}""",
                    _ => null,
                },
            };
        }

        /// <summary>A process for the fake browser to claim to be; ended with the stage.</summary>
        public Process IdleProcess()
        {
            var process = EngineRecordTests.StartIdleProcess();
            _processes.Add(process);
            return process;
        }

        public void Dispose()
        {
            foreach (var process in _processes)
            {
                EngineRecord.Kill(process);
                process.Dispose();
            }
            lock (Launched)
            {
                foreach (var id in Launched)
                {
                    try
                    {
                        // A safety net for a failing test. The stand-in script only ever becomes
                        // "sleep", so nothing else that might since have got this ID is touched.
                        using var launched = Process.GetProcessById(id);
                        if (launched.ProcessName == "sleep" && launched.StartTime > DateTime.Now.AddMinutes(-5))
                        {
                            EngineRecord.Kill(launched);
                        }
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                    {
                        // Gone, as it should be.
                    }
                }
            }
            _folder.Dispose();
        }
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

    [UnixFact]
    public async Task ALauncherThatExitsHandsOverToTheBrowserItStarted()
    {
        // The launched process exits successfully at once, as Firefox's launcher process does
        // by default; the browser turns up afterwards as a different process.
        using var stage = new Stage(then: "exit 0");
        var browser = stage.IdleProcess();
        var engine = new GeckoEngine(stage.Options());
        var snapshots = new List<GeckoSnapshot>();
        var stopped = 0;
        engine.SnapshotReceived += snapshot => { lock (snapshots) snapshots.Add(snapshot); };
        engine.Stopped += () => Interlocked.Increment(ref stopped);

        var start = engine.StartAsync();
        Assert.Same(start, engine.StartAsync());
        Assert.Equal(GeckoStatus.Starting, engine.Status);
        Assert.Null(engine.ProcessId);

        var arguments = await stage.ArgumentsAsync();
        await Task.Delay(300); // long enough for the engine to see the launched process gone
        using var server = await stage.BrowserAsync(browser);
        await start;

        Assert.Equal(GeckoStatus.Running, engine.Status);
        Assert.Equal(browser.Id, engine.ProcessId);
        Assert.NotEqual(stage.Launched.Single(), engine.ProcessId);
        Assert.Equal(66052, engine.WindowHandle);
        Assert.Equal(server.Port, engine.DebuggerPort);
        Assert.Equal(["--profile", Path.GetFullPath(stage.Profile), "--no-remote", "--start-debugger-server", server.Port.ToString()],
            arguments);
        Assert.Equal(32, stage.Token.Length);

        // Both processes are on record for a later Mosaic, should this one die.
        var record = EngineRecord.Load(stage.Profile)!;
        Assert.Equal(stage.Token, record.Token);
        Assert.Equal(ProcessIdentity.Current, record.Owner);
        Assert.Equal(stage.Launched.Single(), record.Launched.Id);
        Assert.Equal(ProcessIdentity.Of(browser), record.Browser);

        await Eventually.TrueAsync(() => { lock (snapshots) return snapshots.Count > 0; });
        lock (snapshots)
        {
            Assert.Equal("Blank", snapshots[0].Tabs.Single().Title);
            Assert.Equal(["newTab"], snapshots[0].Events);
        }
        Assert.Null(await engine.CallAsync("openTab", "about:blank", true)); // the echo is not JSON
        Assert.Same(Task.CompletedTask, engine.StartAsync());

        // The stand-in browser ignores the request to quit, so it has to be terminated.
        engine.StopAndWait(TimeSpan.FromMilliseconds(300));

        Assert.Equal(GeckoStatus.Stopped, engine.Status);
        Assert.Null(engine.ProcessId);
        Assert.True(browser.HasExited);
        Assert.False(File.Exists(Path.Combine(stage.Profile, EngineRecord.FileName)));
        await Eventually.TrueAsync(() => Volatile.Read(ref stopped) == 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CallAsync("version"));
    }

    [UnixFact]
    public async Task ALaunchedProcessThatFailsMeansGeckoFailedToStart()
    {
        using var stage = new Stage(then: "exit 3");
        var engine = new GeckoEngine(stage.Options());

        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<GeckoEngineException>(engine.StartAsync);

        Assert.Equal(GeckoFailure.ExitedDuringStartup, error.Failure);
        Assert.Equal(GeckoStatus.Failed, engine.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "a failure exit should not wait out the hand-off grace");
        engine.StopAndWait(TimeSpan.FromSeconds(1));
    }

    [UnixFact]
    public async Task ASuccessfulExitWithNoBrowserToFollowIsGivenUpOn()
    {
        using var stage = new Stage(then: "exit 0");
        var engine = new GeckoEngine(stage.Options(handOffGrace: TimeSpan.FromMilliseconds(400)));

        var error = await Assert.ThrowsAsync<GeckoEngineException>(engine.StartAsync);

        Assert.Equal(GeckoFailure.ExitedDuringStartup, error.Failure);
        Assert.StartsWith("Gecko quit while it was starting.", engine.FailureMessage);
        engine.StopAndWait(TimeSpan.FromSeconds(1));
    }

    [UnixFact]
    public async Task AGeckoThatDoesNotCarryTheLaunchTokenIsRefused()
    {
        // The launched process stays, as a browser that never opens its port would; what answers
        // on the port is some other Gecko (in real life: a leftover that was handed the command line).
        using var stage = new Stage(then: "exec sleep 600");
        var bystander = stage.IdleProcess();
        var engine = new GeckoEngine(stage.Options());

        var start = engine.StartAsync();
        using var server = await stage.BrowserAsync(bystander, text =>
            text.Contains("setSubstitution") ? throw new FakeGecko.ScriptError("Error: mosaic-wrong-engine") : null);
        var error = await Assert.ThrowsAsync<GeckoEngineException>(() => start);

        Assert.Equal(GeckoFailure.WrongEngine, error.Failure);
        engine.StopAndWait(TimeSpan.FromSeconds(2));
        // The process this launch created is ended; the one that merely answered is not ours to touch.
        Assert.False(IsRunning(stage.Launched.Single()));
        Assert.False(bystander.HasExited);
    }

    [UnixFact]
    public async Task AnOutdatedAgentIsReported()
    {
        using var stage = new Stage(then: "exec sleep 600");
        var engine = new GeckoEngine(stage.Options());

        var start = engine.StartAsync();
        using var server = await stage.BrowserAsync(stage.IdleProcess(), text =>
            text.Contains("setSubstitution") ? """{"agent":1,"gecko":"153.0"}""" : null);
        var error = await Assert.ThrowsAsync<GeckoEngineException>(() => start);

        Assert.Equal(GeckoFailure.AgentOutOfDate, error.Failure);
        engine.StopAndWait(TimeSpan.FromSeconds(2));
        Assert.False(IsRunning(stage.Launched.Single()));
    }

    [UnixFact]
    public async Task StoppingWhileTheLaunchIsInFlightEndsTheLaunchedProcess()
    {
        using var stage = new Stage(then: "exec sleep 600");
        var engine = new GeckoEngine(stage.Options());

        var start = engine.StartAsync();
        engine.Stop(); // before, during or just after process creation; all must end the same way

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        engine.StopAndWait(TimeSpan.FromSeconds(2));
        Assert.Equal(GeckoStatus.Stopped, engine.Status);
        lock (stage.Launched) Assert.All(stage.Launched, id => Assert.False(IsRunning(id)));
    }

    [UnixFact]
    public async Task StoppingOnceTheProcessExistsEndsIt()
    {
        using var stage = new Stage(then: "exec sleep 600");
        var engine = new GeckoEngine(stage.Options());

        var start = engine.StartAsync();
        await stage.ArgumentsAsync();
        var launched = stage.Launched.Single();
        Assert.True(IsRunning(launched));

        engine.StopAndWait(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.False(IsRunning(launched));
        Assert.False(File.Exists(Path.Combine(stage.Profile, EngineRecord.FileName)));

        // And the engine can be started again afterwards.
        var browser = stage.IdleProcess();
        var restart = engine.StartAsync();
        Assert.NotSame(start, restart);
        await Eventually.TrueAsync(() => { lock (stage.Launched) return stage.Launched.Count == 2; });
        using var server = await stage.BrowserAsync(browser);
        await restart;
        Assert.Equal(GeckoStatus.Running, engine.Status);
        engine.StopAndWait(TimeSpan.FromMilliseconds(200));
        lock (stage.Launched) Assert.All(stage.Launched, id => Assert.False(IsRunning(id)));
    }

    [UnixFact]
    public async Task GeckoGoingAwayIsReportedAndCleanedUp()
    {
        using var stage = new Stage(then: "exec sleep 600");
        var browser = stage.IdleProcess();
        var engine = new GeckoEngine(stage.Options());
        var stopped = 0;
        engine.Stopped += () => Interlocked.Increment(ref stopped);

        var start = engine.StartAsync();
        var server = await stage.BrowserAsync(browser);
        await start;

        server.Dispose(); // the debugging connection drops: Gecko is quitting or has crashed

        await Eventually.TrueAsync(() => engine.Status == GeckoStatus.Stopped);
        await Eventually.TrueAsync(() => Volatile.Read(ref stopped) == 1);
        // "Make sure it does": whatever is left of that Gecko is ended after a short grace.
        await Eventually.TrueAsync(() => browser.HasExited && !IsRunning(stage.Launched.Single()));
        engine.StopAndWait(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref stopped));
    }

    [UnixFact]
    public async Task ALeftoverEngineIsTerminatedBeforeStarting()
    {
        using var stage = new Stage(then: "exec sleep 600");
        var leftoverLauncher = stage.IdleProcess();
        var leftoverBrowser = stage.IdleProcess();
        Directory.CreateDirectory(stage.Profile);
        // Written by a Mosaic that has since died without stopping its engine.
        new EngineRecord("old-token", new ProcessIdentity(int.MaxValue - 7, 1),
            ProcessIdentity.Of(leftoverLauncher)!.Value, ProcessIdentity.Of(leftoverBrowser)!.Value).Save(stage.Profile);
        var engine = new GeckoEngine(stage.Options());

        var start = engine.StartAsync();
        using var server = await stage.BrowserAsync(stage.IdleProcess());
        await start;

        Assert.True(leftoverLauncher.HasExited);
        Assert.True(leftoverBrowser.HasExited);
        Assert.Equal(stage.Token, EngineRecord.Load(stage.Profile)!.Token);
        engine.StopAndWait(TimeSpan.FromMilliseconds(200));
    }

    [UnixFact]
    public async Task EventsAndOnStartedArriveOnTheContextTheEngineWasCreatedOn()
    {
        using var stage = new Stage(then: "exec sleep 600");
        using var ui = new SingleThreadContext();
        var threads = new List<(string What, int Thread)>();
        void Note(string what)
        {
            lock (threads) threads.Add((what, Environment.CurrentManagedThreadId));
        }

        var engine = await ui.RunAsync(() => new GeckoEngine(stage.Options()));
        engine.StatusChanged += () => Note("status");
        engine.SnapshotReceived += _ => Note("snapshot");
        engine.Stopped += () => Note("stopped");
        engine.OnStarted = async () =>
        {
            Note("started");
            // Calls work from inside the callback, and its continuation stays on the context.
            await engine.CallAsync("listExtensions");
            Note("started-continued");
        };

        var start = engine.StartAsync();
        using var server = await stage.BrowserAsync(stage.IdleProcess());
        await start;
        await Eventually.TrueAsync(() => { lock (threads) return threads.Any(entry => entry.What == "snapshot"); });
        engine.StopAndWait(TimeSpan.FromMilliseconds(200));
        await Eventually.TrueAsync(() => { lock (threads) return threads.Any(entry => entry.What == "stopped"); });

        lock (threads)
        {
            Assert.All(threads, entry => Assert.True(entry.Thread == ui.ThreadId, $"{entry.What} ran off the context"));
            var order = threads.Select(entry => entry.What).Where(what => what != "snapshot").ToList();
            // Starting, Running, then the callback; Stopped is announced by both events, in that order.
            Assert.Equal(["status", "status", "started", "started-continued", "status", "stopped"], order);
        }
    }

    [UnixFact]
    public async Task AFailingOnStartedIsReportedButLeavesGeckoRunning()
    {
        using var stage = new Stage(then: "exec sleep 600");
        var engine = new GeckoEngine(stage.Options())
        {
            OnStarted = () => throw new InvalidOperationException("extensions would not load"),
        };

        var start = engine.StartAsync();
        using var server = await stage.BrowserAsync(stage.IdleProcess());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => start);

        Assert.Equal("extensions would not load", error.Message);
        Assert.Equal(GeckoStatus.Running, engine.Status);
        engine.StopAndWait(TimeSpan.FromMilliseconds(200));
    }
}
