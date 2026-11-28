using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;
using Mosaic.Core.Productivity;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Productivity;

/// <summary>
/// The productivity parts of the shared agent (<c>Resources/gecko/agent.sys.mjs</c>) against the
/// Firefox named by <c>MOSAIC_GECKO_EXE</c> (the Windows CI runner's): the shortcut forwarding
/// table Mosaic computes, and Gecko's reader view. Headless, throwaway profile.
/// </summary>
public sealed class GeckoProductivityIntegrationTests : IDisposable
{
    private readonly TemporaryDirectory profile = new();
    private readonly List<GeckoEngine> engines = [];

    static GeckoProductivityIntegrationTests()
    {
        Environment.SetEnvironmentVariable("MOZ_APP_NO_DOCK", "1");
    }

    private static string AgentDirectory
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, "Resources", "gecko");
                if (File.Exists(Path.Combine(candidate, "agent.sys.mjs"))) return candidate;
            }
            throw new DirectoryNotFoundException("Resources/gecko/agent.sys.mjs was not found above the test folder.");
        }
    }

    private GeckoEngine NewEngine()
    {
        var engine = new GeckoEngine(new GeckoEngineOptions
        {
            ProfileDirectory = profile.File("profile"),
            AgentDirectory = AgentDirectory,
            Runtime = GeckoRuntime.Locate()!,
            ExtraArguments = ["--headless"],
            PollInterval = TimeSpan.FromMilliseconds(50),
        });
        engines.Add(engine);
        return engine;
    }

    public void Dispose()
    {
        foreach (var engine in engines) engine.StopAndWait(TimeSpan.FromSeconds(5));
        profile.Dispose();
    }

    [GeckoFact]
    public async Task ShortcutsInTheForwardingTableArriveAsProductivityEvents()
    {
        // The table exactly as Mosaic computes it for the default Windows shortcuts.
        JsonArray table;
        using (var fixture = ProductivityTestSupport.Create())
        {
            table = new JsonArray(fixture.Model.Productivity.Shortcuts.GeckoForwardingPairs()
                .Select(pair => (JsonNode)new JsonArray(pair.Chord, pair.EventType)).ToArray());
        }
        var engine = NewEngine();
        var events = new List<string>();
        engine.SnapshotReceived += snapshot => { lock (events) events.AddRange(snapshot.Events); };
        await engine.StartAsync();
        Assert.Equal(table.Count, (await engine.CallAsync("setForwardedShortcuts", table))!.GetValue<int>());

        // Key presses as Windows delivers them to the chrome window (Ctrl is the accelerator off macOS).
        var script = """
            (() => {
              const win = Services.wm.getMostRecentWindow("navigator:browser");
              const mac = Services.appinfo.OS === "Darwin";
              const root = win.document.documentElement;
              const press = (key, { accel = false, shift = false, alt = false, code = "" } = {}) => {
                let arrived = false;
                const listener = () => { arrived = true; };
                root.addEventListener("keydown", listener, true);
                root.dispatchEvent(new win.KeyboardEvent("keydown", {
                  key, code, bubbles: true, cancelable: true,
                  metaKey: accel && mac, ctrlKey: accel && !mac, shiftKey: shift, altKey: alt,
                }));
                root.removeEventListener("keydown", listener, true);
                return !arrived;
              };
              const tabs = win.gBrowser.tabs.length;
              return JSON.stringify({
                intercepted: {
                  palette: press("k", { accel: true }),
                  reload: press("F5"),
                  reader: press("F9"),
                  nextTab: press("Tab", { accel: true }),
                  previousTab: press("Tab", { accel: true, shift: true }),
                  back: press("ArrowLeft", { alt: true, code: "ArrowLeft" }),
                  lastTab: press("9", { accel: true }),
                  plainK: press("k"),
                  newTabBuiltIn: press("t", { accel: true }),
                },
                tabsOpened: win.gBrowser.tabs.length - tabs,
              });
            })()
            """;
        using var keyboard = await RdpClient.ConnectTcpAsync("127.0.0.1", engine.DebuggerPort!.Value);
        await keyboard.AttachToParentProcessAsync();
        var pressed = JsonNode.Parse((await keyboard.EvaluateAsync(script))!.GetValue<string>())!;
        var intercepted = pressed["intercepted"]!.AsObject().ToDictionary(e => e.Key, e => e.Value!.GetValue<bool>());
        Assert.All(["palette", "reload", "reader", "nextTab", "previousTab", "back", "lastTab", "newTabBuiltIn"],
            key => Assert.True(intercepted[key], key));
        Assert.False(intercepted["plainK"]);
        Assert.Equal(0, pressed["tabsOpened"]!.GetValue<int>());

        string[] expected =
        [
            ShortcutManager.GeckoEventPrefix + ShortcutCommands.CommandPalette,
            ShortcutManager.GeckoEventPrefix + ShortcutCommands.Reload,
            ShortcutManager.GeckoEventPrefix + ShortcutCommands.ReaderView,
            ShortcutManager.GeckoEventPrefix + ShortcutCommands.NextTab,
            ShortcutManager.GeckoEventPrefix + ShortcutCommands.PreviousTab,
            ShortcutManager.GeckoEventPrefix + ShortcutCommands.Back,
            ShortcutManager.GeckoEventPrefix + ShortcutCommands.SelectLastTab,
            "newTab",
        ];
        await Eventually.TrueAsync(() => { lock (events) return events.Count >= expected.Length; }, because: "the shortcut events never arrived");
        lock (events) Assert.Equal(expected, events);
    }

    [GeckoFact]
    public async Task GeckosReaderViewJudgesTogglesAndKeepsMosaicsSettings()
    {
        using var server = new ArticleServer();
        var engine = NewEngine();
        GeckoSnapshot? latest = null;
        engine.SnapshotReceived += snapshot => latest = snapshot;
        await engine.StartAsync();

        var settings = new ReaderSettings { Font = ReaderFont.Serif, FontSize = 7, ContentWidth = 4, LineHeight = 6, Theme = ReaderTheme.Sepia };
        await engine.CallAsync("setReaderPrefs", settings.GeckoPreferences());
        var prefs = (JsonObject)(await engine.CallAsync("readerPrefs"))!;
        Assert.Equal(settings, new ReaderSettings().MergingGeckoPreferences(prefs));

        var id = (await engine.CallAsync("openTab", server.Url, true))!.GetValue<int>();
        await Eventually.TrueAsync(() => latest?.Tabs.Any(t => t.Id == id && !t.Loading && t.Url == server.Url) == true, 20, "the article never loaded");
        var judged = false;
        for (var attempt = 0; attempt < 100 && !judged; attempt++)
        {
            judged = (await engine.CallAsync("readerInfo", id))?["available"]?.GetValue<bool>() == true;
            if (!judged) await Task.Delay(200);
        }
        Assert.True(judged, "Gecko never judged the page to be an article");

        await engine.CallAsync("setReaderMode", id, true);
        await Eventually.TrueAsync(() => latest?.Tabs.Any(t => t.Id == id && t.Url.StartsWith(ReaderMode.GeckoReaderPrefix, StringComparison.Ordinal)) == true,
            20, "the tab never showed about:reader");
        var reading = latest!.Tabs.Single(t => t.Id == id);
        Assert.Equal(server.Url, ReaderMode.OriginalUrl(reading.Url));
        Assert.True((await engine.CallAsync("readerInfo", id))!["active"]!.GetValue<bool>());

        await engine.CallAsync("setReaderMode", id, false);
        await Eventually.TrueAsync(() => latest?.Tabs.Any(t => t.Id == id && t.Url == server.Url) == true, 20, "the tab never left reader view");
    }

    /// <summary>Serves one long article over loopback HTTP (Gecko's reader only judges http(s) pages).</summary>
    private sealed class ArticleServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);

        public ArticleServer()
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/article";
            _ = AcceptAsync();
        }

        public string Url { get; }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = ServeAsync(client);
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await reader.ReadLineAsync(patience.Token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(patience.Token))) { }
                var paragraph = "<p>Mosaic is a web browser with two engines. Every tab runs on Mozilla's Gecko or on "
                    + "Chromium's Blink, and a tab can move from one engine to the other at any time, reloading its page there. "
                    + "This paragraph exists to give the reader view enough text to consider the page an article worth reading.</p>";
                var body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>A long article</title></head>"
                    + "<body><header><nav><a href=\"/\">Home</a></nav></header><main><article><h1>A long article</h1>"
                    + string.Concat(Enumerable.Repeat(paragraph, 12)) + "</article></main><footer>Footer</footer></body></html>");
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n"
                    + "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
        }

        public void Dispose() => listener.Stop();
    }
}
