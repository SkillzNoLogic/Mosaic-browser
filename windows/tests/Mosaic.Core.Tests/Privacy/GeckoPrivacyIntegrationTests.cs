using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Privacy;
using Mosaic.Core.Tests.Blocker;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Privacy;

/// <summary>
/// The privacy features in a real Gecko (the Firefox named by <c>MOSAIC_GECKO_EXE</c>, headless,
/// throwaway profile) with the shared agent and uBlock Origin's real engine: preferences from
/// <c>configurePrivacy</c>, cosmetic filtering in the page, the dashboard's filter attribution,
/// and site data listing and clearing. Runs on the Windows CI runner; skipped elsewhere.
/// </summary>
public sealed class GeckoPrivacyIntegrationTests : IDisposable
{
    private readonly TemporaryDirectory profile = new();
    private readonly List<GeckoEngine> engines = [];

    static GeckoPrivacyIntegrationTests() => Environment.SetEnvironmentVariable("MOZ_APP_NO_DOCK", "1");

    public void Dispose()
    {
        foreach (var engine in engines) engine.StopAndWait(TimeSpan.FromSeconds(5));
        profile.Dispose();
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
            Runtime = GeckoRuntime.Locate(),
            ExtraArguments = ["--headless"],
            PollInterval = TimeSpan.FromMilliseconds(50),
        });
        engines.Add(engine);
        return engine;
    }

    [GeckoBlockerFact]
    public async Task PrivacyFeaturesWorkInGecko()
    {
        using var server = new PrivacyTestServer();
        var engine = NewEngine();
        var lists = profile.File("Blocker/Lists");
        Directory.CreateDirectory(lists);
        new FilterListStore(lists, []).SaveUserFilters("##.mosaic-generic-ad\n127.0.0.1##.mosaic-site-ad");
        var blocker = new ContentBlocker(engine, new BrowserSettings(profile.File("settings.json")), new ContentBlockerOptions
        {
            ResourcesDirectory = UBlockBundle.Directory!,
            ListsDirectory = lists,
            RefreshDelay = TimeSpan.FromDays(1),
        });
        var privacy = new PrivacyController(engine, blocker, profile.File("privacy.json"));
        try
        {
            blocker.Start();
            engine.OnStarted = blocker.ConfigureGeckoAsync;
            privacy.Start(null);
            privacy.Update(settings => settings with
            {
                HttpsOnly = true,
                DnsOverHttps = new DnsOverHttpsSetting { Mode = DnsMode.Automatic, ProviderId = "quad9" },
            });
            await engine.StartAsync();

            // Preferences: what configurePrivacy set, as Gecko reports them.
            var status = (await engine.CallAsync("privacyStatus"))!;
            if (status["blocker"]?["engine"]?.GetValue<bool>() != true)
            {
                // ContentBlocker swallows the agent's error; ask again to see it.
                string reason;
                try
                {
                    var paths = new JsonArray([.. blocker.Store.AvailableListPaths().Select(path => (JsonNode)JsonValue.Create(path))]);
                    reason = (await engine.CallAsync("configureBlocker", Path.Combine(UBlockBundle.Directory!, "ublock-engine.js"),
                        blocker.Store.SuffixListPath, paths, true, new JsonArray()))?.ToJsonString() ?? "null";
                }
                catch (Exception error)
                {
                    reason = error.Message;
                }
                Assert.Fail($"The blocker did not load into Gecko. Lists: {string.Join(", ", blocker.Store.AvailableListPaths().Select(Path.GetFileName))}. Again: {reason}");
            }
            var prefs = status["prefs"]!;
            Assert.True(prefs["dom.security.https_only_mode"]!.GetValue<bool>());
            Assert.True(prefs["privacy.query_stripping.enabled"]!.GetValue<bool>());
            Assert.Equal(1, prefs["network.cookie.cookieBehavior"]!.GetValue<int>());
            Assert.Equal(2, prefs["network.trr.mode"]!.GetValue<int>());
            Assert.Equal("https://dns.quad9.net/dns-query", prefs["network.trr.uri"]!.GetValue<string>());
            Assert.True(status["cosmetic"]!["listening"]!.GetValue<bool>());

            // The page: its ad script is blocked, its decoy elements hidden by cosmetic filters
            // (the page reports what it sees in its title), and it sets a cookie.
            var id = (await engine.CallAsync("openTab", server.Url, true))!.GetValue<int>();
            var title = "";
            var clock = Stopwatch.StartNew();
            while (!title.StartsWith("cosmetic:", StringComparison.Ordinal))
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the test page never reported its cosmetic state (title \"{title}\")");
                await Task.Delay(100);
                title = (await engine.CallAsync("pageStatus", id))?["title"]?.GetValue<string>() ?? "";
            }
            if (title != "cosmetic:hidden,hidden")
            {
                // What the parent saw: whether content processes asked for cosmetic rules at all.
                var after = await engine.CallAsync("privacyStatus");
                var version = await engine.CallAsync("version");
                Assert.Fail($"cosmetic filtering did not hide the decoys ({title}); Gecko {version?.ToJsonString()}; status {after?.ToJsonString()}");
            }

            var tab = new BrowserTab(EngineKind.Gecko) { GeckoId = id };
            var blocked = await blocker.BlockedRequestsAsync(tab);
            var ad = Assert.Single(blocked, request => request.Url == PrivacyTestServer.AdScript);
            Assert.Equal("script", ad.Type);
            Assert.NotEqual("", ad.Filter);

            // Site data: the page's cookie is listed, then cleared.
            await privacy.LoadSiteDataAsync();
            Assert.True(privacy.SiteDataIncludesGecko);
            var site = Assert.Single(privacy.SiteData, entry => entry.Site == "127.0.0.1");
            Assert.True(site.GeckoCookies >= 1);
            await privacy.ClearSiteDataAsync(site);
            Assert.DoesNotContain(privacy.SiteData, entry => entry.Site == "127.0.0.1" && entry.GeckoCookies > 0);

            var summary = await privacy.ClearBrowsingDataAsync(new HashSet<BrowsingDataKind> { BrowsingDataKind.History, BrowsingDataKind.Cache }, BrowsingDataTimeRange.LastHour, new BrowsingDataRegistry());
            Assert.Contains("Mozilla", summary);
            Assert.DoesNotContain("next starts", summary);
        }
        finally
        {
            blocker.Shutdown();
        }
    }
}

/// <summary>
/// A page for privacy checks on a loopback port: it asks for a well-known ad script (never
/// contacted: a blocked request is cancelled first), has two decoy elements for cosmetic filters
/// (<c>.mosaic-generic-ad</c>, <c>.mosaic-site-ad</c>), sets a cookie, and reports in its title
/// whether the decoys are hidden ("cosmetic:hidden,hidden"). Works the same in both engines.
/// </summary>
internal sealed class PrivacyTestServer : IDisposable
{
    public const string AdScript = "http://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js";

    public const string Page = """
        <!doctype html><meta charset="utf-8"><title>privacy test</title>
        <div class="mosaic-generic-ad" id="generic">generic ad</div>
        <div class="mosaic-site-ad" id="site">site ad</div>
        <script src="http://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js"></script>
        <script>
        document.cookie = "mosaic-privacy=1; path=/; max-age=3600";
        let tries = 0;
        (function check() {
          const hidden = id => getComputedStyle(document.getElementById(id)).display === "none" ? "hidden" : "shown";
          const state = hidden("generic") + "," + hidden("site");
          if (state === "hidden,hidden" || ++tries > 100) document.title = "cosmetic:" + state;
          else setTimeout(check, 50);
        })();
        </script>
        """;

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);

    public PrivacyTestServer()
    {
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        _ = AcceptAsync();
    }

    public string Url { get; }

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
            var requestLine = await reader.ReadLineAsync(patience.Token);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(patience.Token))) { }
            var isPage = requestLine?.StartsWith("GET / ", StringComparison.Ordinal) == true
                || requestLine?.StartsWith("GET /?", StringComparison.Ordinal) == true;
            var body = Encoding.UTF8.GetBytes(isPage ? Page : "");
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(isPage ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
        {
        }
    }

    public void Dispose() => listener.Stop();
}
