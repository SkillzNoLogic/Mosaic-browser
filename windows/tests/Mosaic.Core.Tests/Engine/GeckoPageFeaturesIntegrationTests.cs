using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Engine;

/// <summary>
/// The engine area's agent methods (find, zoom, PDF, screenshots, mute, permission requests,
/// blocked pop-ups) against the real Gecko named by <c>MOSAIC_GECKO_EXE</c>: headless, on a
/// throwaway profile, over the transport Windows uses. On CI this runs on the Windows runner
/// with its Firefox, which is how the Gecko side of these features is verified there.
/// </summary>
public sealed class GeckoPageFeaturesIntegrationTests : IDisposable
{
    private readonly TemporaryFolder folder = new();
    private readonly List<GeckoEngine> engines = [];
    private readonly List<string> events = [];

    static GeckoPageFeaturesIntegrationTests() => Environment.SetEnvironmentVariable("MOZ_APP_NO_DOCK", "1");

    public void Dispose()
    {
        foreach (var engine in engines) engine.StopAndWait(TimeSpan.FromSeconds(5));
        folder.Dispose();
    }

    private static string AgentDirectory
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "Resources", "gecko");
                if (File.Exists(Path.Combine(candidate, "agent.sys.mjs"))) return candidate;
            }
            throw new DirectoryNotFoundException("Resources/gecko/agent.sys.mjs was not found above the test folder.");
        }
    }

    private async Task<GeckoEngine> StartAsync()
    {
        var engine = new GeckoEngine(new GeckoEngineOptions
        {
            ProfileDirectory = folder.File("profile"),
            AgentDirectory = AgentDirectory,
            Runtime = GeckoRuntime.Locate(),
            ExtraArguments = ["--headless"],
            PollInterval = TimeSpan.FromMilliseconds(50),
        });
        engines.Add(engine);
        engine.SnapshotReceived += snapshot =>
        {
            lock (events) events.AddRange(snapshot.Events);
        };
        await engine.StartAsync();
        return engine;
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> probe, string what, double seconds = 30) where T : class
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await probe() is { } value) return value;
            Assert.True(clock.Elapsed.TotalSeconds < seconds, $"Gecko never got to: {what}");
            await Task.Delay(100);
        }
    }

    private async Task<JsonArray> PageEventsAsync(GeckoEngine engine, string type) =>
        await Eventually(async () =>
        {
            bool seen;
            lock (events) seen = events.Contains(type);
            if (!seen) return null;
            var taken = await engine.CallAsync("takePageEvents") as JsonArray;
            return taken is { Count: > 0 } ? taken : null;
        }, $"a {type} event");

    private static async Task WaitForTitleAsync(GeckoEngine engine, int tab, string title) =>
        await Eventually(async () =>
        {
            var state = JsonNode.Parse((await engine.CallAsync("state"))?.ToJsonString() ?? "{}");
            return state?["tabs"] is JsonArray tabs && tabs.Any(t => t!["id"]!.GetValue<int>() == tab && t["title"]!.GetValue<string>() == title && !t["loading"]!.GetValue<bool>())
                ? "loaded" : null;
        }, $"the page titled {title}");

    [GeckoFact]
    public async Task FindZoomPdfScreenshotsAndMuteWorkThroughTheAgent()
    {
        using var server = new Pages();
        var engine = await StartAsync();
        Assert.True((await engine.CallAsync("configurePageFeatures", new JsonObject { ["fakeMedia"] = true, ["keepEvents"] = true }))!.GetValue<bool>());

        var tab = (await engine.CallAsync("openTab", server.Url + "text", true))!.GetValue<int>();
        await WaitForTitleAsync(engine, tab, "Find me");

        // Find: every match counted, the first one current; case-sensitive narrows it down.
        var found = await engine.CallAsync("find", tab, "mosaic", new JsonObject { ["again"] = false, ["backwards"] = false, ["caseSensitive"] = false });
        Assert.True(found!["found"]!.GetValue<bool>(), found.ToJsonString());
        Assert.Equal(3, found["total"]!.GetValue<int>());
        Assert.Equal(1, found["current"]!.GetValue<int>());
        var next = await engine.CallAsync("find", tab, "mosaic", new JsonObject { ["again"] = true, ["backwards"] = false, ["caseSensitive"] = false });
        Assert.Equal(2, next!["current"]!.GetValue<int>());
        var exact = await engine.CallAsync("find", tab, "Mosaic", new JsonObject { ["again"] = false, ["caseSensitive"] = true });
        Assert.Equal(1, exact!["total"]!.GetValue<int>());
        var none = await engine.CallAsync("find", tab, "zebra", new JsonObject());
        Assert.False(none!["found"]!.GetValue<bool>());
        Assert.True((await engine.CallAsync("stopFind", tab))!.GetValue<bool>());

        // Zoom and mute, as pageActivity reports them back.
        Assert.Equal(1.5, (await engine.CallAsync("setZoom", tab, 1.5))!.GetValue<double>(), 2);
        Assert.True((await engine.CallAsync("setMuted", tab, true))!.GetValue<bool>());
        var activity = (await engine.CallAsync("pageActivity") as JsonArray)!.Single(entry => entry!["id"]!.GetValue<int>() == tab)!;
        Assert.Equal(1.5, activity["zoom"]!.GetValue<double>(), 2);
        Assert.True(activity["muted"]!.GetValue<bool>());
        await engine.CallAsync("setZoom", tab, 1.0);

        // PDF, without a dialog.
        var pdf = folder.File("page.pdf");
        await engine.CallAsync("saveAsPDF", tab, pdf);
        await Eventually(() => Task.FromResult(File.Exists(pdf) && new FileInfo(pdf).Length > 4 ? "saved" : null), "the PDF");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));

        // Screenshots: the visible area, then the whole (tall) page.
        var visible = folder.File("visible.png");
        var shot = await engine.CallAsync("screenshot", tab, visible, false);
        Assert.True(shot!["width"]!.GetValue<int>() > 0, shot.ToJsonString());
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(visible).Take(4));
        var full = folder.File("full.png");
        var fullShot = await engine.CallAsync("screenshot", tab, full, true);
        Assert.True(fullShot!["height"]!.GetValue<int>() > shot["height"]!.GetValue<int>(), fullShot.ToJsonString());
        Assert.False(fullShot["cropped"]!.GetValue<bool>());
    }

    [GeckoFact]
    public async Task PermissionRequestsAndBlockedPopupsReachMosaicWithTheirDetails()
    {
        using var server = new Pages();
        var engine = await StartAsync();
        await engine.CallAsync("configurePageFeatures", new JsonObject { ["fakeMedia"] = true, ["keepEvents"] = true });

        // Location: Mosaic is asked (not Firefox's panel), and its answer reaches the page.
        var tab = (await engine.CallAsync("openTab", server.Url + "location", true))!.GetValue<int>();
        var requests = await PageEventsAsync(engine, "permissionRequest");
        var request = requests.First(entry => entry!["type"]!.GetValue<string>() == "permissionRequest")!;
        Assert.Equal(tab, request["tab"]!.GetValue<int>());
        var detail = JsonNode.Parse(request["detail"]!.GetValue<string>())!;
        Assert.Equal(["location"], detail["kinds"]!.AsArray().Select(kind => kind!.GetValue<string>()));
        Assert.StartsWith("http://127.0.0.1:", detail["origin"]!.GetValue<string>());
        Assert.True((await engine.CallAsync("answerPermission", detail["id"]!.GetValue<int>(), false))!.GetValue<bool>());
        await WaitForTitleAsync(engine, tab, "denied");

        // A remembered decision answers without asking.
        await engine.CallAsync("setSitePermissions", new JsonArray(new JsonObject
        {
            ["origin"] = detail["origin"]!.GetValue<string>(),
            ["permission"] = "location",
            ["decision"] = "allow",
        }));
        await engine.CallAsync("navigate", tab, server.Url + "location?again");
        await WaitForTitleAsync(engine, tab, "allowed");

        // A pop-up without a click: blocked, and reported with its address.
        await engine.CallAsync("navigate", tab, server.Url + "popup");
        var blocked = await PageEventsAsync(engine, "popupBlocked");
        var popup = JsonNode.Parse(blocked.First(entry => entry!["type"]!.GetValue<string>() == "popupBlocked")!["detail"]!.GetValue<string>())!;
        Assert.Equal(1, popup["count"]!.GetValue<int>());
        Assert.EndsWith("/text", popup["urls"]![0]!.GetValue<string>());
    }

    /// <summary>The test pages, on a loopback port (127.0.0.1 is a secure context, so location may be asked).</summary>
    private sealed class Pages : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);

        public Pages()
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            _ = AcceptAsync();
        }

        public string Url { get; }

        private static string Page(string path) => path switch
        {
            "/text" => "<!doctype html><title>Find me</title><p>mosaic one</p><p>Mosaic two</p><p>MOSAIC three</p>"
                + "<div style=\"height:3000px\"></div><p>the end</p>",
            "/popup" => "<!doctype html><title>Pop-up</title><script>window.open('/text', '_blank');</script>",
            _ when path.StartsWith("/location", StringComparison.Ordinal) =>
                "<!doctype html><title>asking</title><script>navigator.geolocation.getCurrentPosition("
                + "() => { document.title = 'allowed'; }, () => { document.title = 'denied'; });</script>",
            _ => "<!doctype html><title>Nothing</title>",
        };

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
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
                var requestLine = await reader.ReadLineAsync(patience.Token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(patience.Token))) { }
                var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                var body = Encoding.UTF8.GetBytes(Page(path));
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
                    "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
            {
            }
        }

        public void Dispose() => listener.Stop();
    }
}
