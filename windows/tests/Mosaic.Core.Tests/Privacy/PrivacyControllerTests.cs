using System.Text.Json.Nodes;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;
using Mosaic.Core.Privacy;
using Mosaic.Core.Tests.Blocker;
using Mosaic.Core.Tests.Browser;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Privacy;

/// <summary>A Chromium engine that can also open private browsers.</summary>
internal sealed class FakePrivateChromiumEngine : IChromiumEngine, IPrivateChromiumEngine
{
    public bool IsAvailable { get; set; } = true;
    public string? UnavailableReason => IsAvailable ? null : "No WebView2 here.";
    public List<FakeChromiumBrowser> Created { get; } = [];
    public List<FakeChromiumBrowser> CreatedPrivate { get; } = [];

    public IChromiumBrowser CreateBrowser(string url)
    {
        var browser = new FakeChromiumBrowser(url);
        Created.Add(browser);
        return browser;
    }

    public IChromiumBrowser CreatePrivateBrowser(string url)
    {
        var browser = new FakeChromiumBrowser(url);
        CreatedPrivate.Add(browser);
        return browser;
    }
}

/// <summary>Records what the privacy area asks of Chromium.</summary>
internal sealed class FakeChromiumPrivacy : IChromiumPrivacy
{
    public bool IsAvailable { get; set; } = true;
    public List<ChromiumCookie> Cookies { get; } = [];
    public List<(IReadOnlySet<BrowsingDataKind> Kinds, DateTimeOffset? Since)> Cleared { get; } = [];
    public List<(string Site, IReadOnlyList<string> Origins)> ClearedSites { get; } = [];
    public List<PrivacySettings> Applied { get; } = [];
    public PrivacySettings? StartedWith { get; set; }

    public Task<IReadOnlyList<ChromiumCookie>> CookiesAsync(bool privateProfile = false) =>
        Task.FromResult<IReadOnlyList<ChromiumCookie>>(privateProfile ? [] : [.. Cookies]);

    public Task ClearAsync(IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since)
    {
        Cleared.Add((kinds, since));
        return Task.CompletedTask;
    }

    public Task ClearSiteAsync(string site, IReadOnlyList<string> origins)
    {
        ClearedSites.Add((site, origins));
        Cookies.RemoveAll(cookie => cookie.Domain.TrimStart('.') == site || cookie.Domain.EndsWith("." + site, StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    public void Apply(PrivacySettings settings)
    {
        Applied.Add(settings);
        StartedWith ??= settings;
    }

    public bool NeedsRestart(PrivacySettings settings) => StartedWith is { } start && start.DnsOverHttps != settings.DnsOverHttps;
    public int PrivateBrowserCount { get; set; }
    public bool HasPrivateProfile => PrivateBrowserCount > 0;
    public JsonObject Status() => new() { ["fake"] = true };
}

/// <summary>A browser model with a private-capable Chromium, a fake Gecko and its privacy settings in a throwaway folder.</summary>
internal sealed class PrivacyFixture : IDisposable
{
    private readonly TemporaryDirectory root = new();

    public FakeGeckoEngine Gecko { get; } = new();
    public FakePrivateChromiumEngine Chromium { get; } = new();
    public FakeChromiumPrivacy ChromiumPrivacy { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public BrowserModel Model { get; }

    public PrivacyFixture(bool chromiumAvailable = true)
    {
        Chromium.IsAvailable = chromiumAvailable;
        var settings = new BrowserSettings(root.File("settings.json"));
        var rules = TestRules.Create();
        var extensions = new ExtensionManager(Gecko, () => new FakeExtensionLibrary(), settings, new FakeChromiumExtensionHost(), new StubHttpHandler(), rules);
        Model = new BrowserModel(Gecko, Chromium, extensions, settings, rules, Time, new ContentBlockerOptions
        {
            ResourcesDirectory = root.File("blocker"),
            ListsDirectory = root.File("Blocker/Lists"),
            HttpHandler = new StubHttpHandler { Respond = _ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)) },
        })
        {
            PrivacySettingsPath = root.File("privacy.json"),
        };
    }

    public string File(string name) => root.File(name);

    public void Dispose() => root.Dispose();
}

public sealed class PrivateTabTests : IDisposable
{
    private readonly PrivacyFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    [Fact]
    public void PrivateTabsAreChromiumInPrivateAndMarked()
    {
        var tab = fixture.Model.NewPrivateTab("https://example.com/");
        Assert.NotNull(tab);
        Assert.True(tab.IsPrivate);
        Assert.Equal(EngineKind.Chromium, tab.Engine);
        Assert.Same(tab, fixture.Model.SelectedTab);
        Assert.Single(fixture.Chromium.CreatedPrivate);
        Assert.Empty(fixture.Chromium.Created);
        Assert.False(fixture.Model.Tabs[0].IsPrivate);
        Assert.Single(fixture.Model.PrivateTabList);

        // Loading again reuses its private browser; a fresh browser after a failure is private too.
        fixture.Model.Load("https://example.org/", tab);
        Assert.Single(fixture.Chromium.CreatedPrivate);
        fixture.Chromium.CreatedPrivate[0].RaiseFailed("crashed");
        fixture.Model.Load("https://example.org/", tab);
        Assert.Equal(2, fixture.Chromium.CreatedPrivate.Count);
        Assert.Empty(fixture.Chromium.Created);
    }

    [Fact]
    public void APrivateTabCannotMoveToGecko()
    {
        var tab = fixture.Model.NewPrivateTab("https://example.com/")!;
        fixture.Model.SetEngine(EngineKind.Gecko);
        Assert.Equal(EngineKind.Chromium, tab.Engine);
        Assert.Equal(PrivacyController.GeckoPrivateNotice, fixture.Model.Notice);
        Assert.Empty(fixture.Gecko.CallsTo("openTab"));
    }

    [Fact]
    public void WithoutChromiumThereAreNoPrivateTabs()
    {
        using var noChromium = new PrivacyFixture(chromiumAvailable: false);
        Assert.Null(noChromium.Model.NewPrivateTab("https://example.com/"));
        Assert.Contains("Chromium", noChromium.Model.Notice);
        Assert.Single(noChromium.Model.Tabs);
    }

    [Fact]
    public async Task CtrlShiftNInsideGeckoOpensAPrivateTab()
    {
        await fixture.Gecko.StartAsync();
        fixture.Gecko.ReportEvents([], "newPrivateTab");
        Assert.Contains(fixture.Model.Tabs, tab => tab.IsPrivate);
    }

    [Fact]
    public void SecurityIndicatorFollowsTheSelectedTab()
    {
        Assert.Equal(ConnectionSecurity.NotApplicable, fixture.Model.SelectedConnectionSecurity);
        fixture.Model.NewTab("http://example.com/", EngineKind.Chromium);
        Assert.Equal(ConnectionSecurity.Insecure, fixture.Model.SelectedConnectionSecurity);
    }
}

public sealed class PrivacyControllerTests : IDisposable
{
    private readonly PrivacyFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    private PrivacyController Privacy => fixture.Model.Privacy;

    [Fact]
    public void SettingsAreSavedAppliedAndReadBack()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        Privacy.Update(settings => settings with { HttpsOnly = true });
        Assert.True(fixture.ChromiumPrivacy.Applied[^1].HttpsOnly);
        var reread = new PrivacyController(fixture.Gecko, fixture.Model.Blocker, fixture.File("privacy.json"));
        Assert.True(reread.Settings.HttpsOnly);
        Assert.Contains("\"httpsOnly\": true", File.ReadAllText(fixture.File("privacy.json")));
    }

    [Fact]
    public void DnsChangesNeedAChromiumRestart()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        Assert.False(Privacy.ChromiumNeedsRestart);
        Privacy.Update(settings => settings with { DnsOverHttps = new DnsOverHttpsSetting { Mode = DnsMode.Strict } });
        Assert.True(Privacy.ChromiumNeedsRestart);
    }

    [Fact]
    public async Task GeckoGetsThePreferencesOnEveryStartAndAfterChanges()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        await fixture.Gecko.StartAsync();
        var options = Assert.IsType<JsonObject>(Assert.Single(fixture.Gecko.CallsTo("configurePrivacy"))[0]);
        Assert.False(options["httpsOnly"]!.GetValue<bool>());
        Assert.Contains("fbclid", options["stripList"]!.AsArray().Select(name => name!.GetValue<string>()));
        Assert.Equal(5, options["dnsPreferences"]!["network.trr.mode"]!.GetValue<int>());
        Assert.True(options["cosmetic"]!.GetValue<bool>());

        Privacy.Update(settings => settings with { HttpsOnly = true, DnsOverHttps = new DnsOverHttpsSetting { Mode = DnsMode.Automatic, ProviderId = "quad9" } });
        var latest = Assert.IsType<JsonObject>(fixture.Gecko.CallsTo("configurePrivacy")[^1][0]);
        Assert.True(latest["httpsOnly"]!.GetValue<bool>());
        Assert.Equal("https://dns.quad9.net/dns-query", latest["dnsPreferences"]!["network.trr.uri"]!.GetValue<string>());

        Privacy.AllowHttpThisSession("www.legacy.example");
        var exceptions = fixture.Gecko.CallsTo("configurePrivacy")[^1][0] as JsonObject;
        Assert.Contains("legacy.example", exceptions!["httpsExceptions"]!.AsArray().Select(site => site!.GetValue<string>()));
        Assert.Null(Privacy.Rewrite("http://legacy.example/"));
        Assert.Equal("https://other.example/", Privacy.Rewrite("http://other.example/"));
    }

    [Fact]
    public async Task ClearOnExitClearsGeckoAtStart()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        Privacy.Update(settings => settings with { ClearCookiesOnExit = true });
        await fixture.Gecko.StartAsync();
        var call = Assert.Single(fixture.Gecko.CallsTo("clearBrowsingData"));
        Assert.Equal(["cookiesAndSiteData"], ((JsonArray)call[0]!).Select(kind => kind!.GetValue<string>()));
    }

    [Fact]
    public async Task ClearingReachesBothEnginesAndRegisteredServices()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        var registry = new BrowsingDataRegistry();
        var history = new RecordingStore("Mosaic history", BrowsingDataKind.History);
        registry.Register(history);
        await fixture.Gecko.StartAsync();
        var now = fixture.Time.GetUtcNow();

        var kinds = new HashSet<BrowsingDataKind> { BrowsingDataKind.History, BrowsingDataKind.Cache };
        var summary = await Privacy.ClearBrowsingDataAsync(kinds, BrowsingDataTimeRange.LastHour, registry);

        Assert.Equal("Cleared 2 kinds of data (last hour) from Chromium, Mozilla, Mosaic history.", summary);
        Assert.Equal(summary, Privacy.LastClearSummary);
        var chromium = Assert.Single(fixture.ChromiumPrivacy.Cleared);
        Assert.Equal(now.AddHours(-1), chromium.Since);
        var gecko = Assert.Single(fixture.Gecko.CallsTo("clearBrowsingData"));
        Assert.Equal(now.AddHours(-1).ToUnixTimeMilliseconds(), gecko[1]);
        Assert.Equal(now.AddHours(-1), Assert.Single(history.Calls).Since);
    }

    [Fact]
    public async Task GeckoIsClearedWhenItNextStartsIfItIsNotRunning()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        var summary = await Privacy.ClearBrowsingDataAsync(new HashSet<BrowsingDataKind> { BrowsingDataKind.CookiesAndSiteData }, BrowsingDataTimeRange.AllTime, new BrowsingDataRegistry());
        Assert.Contains("Mozilla (when it next starts)", summary);
        Assert.True(Privacy.HasPendingGeckoClear);
        await fixture.Gecko.StartAsync();
        var call = Assert.Single(fixture.Gecko.CallsTo("clearBrowsingData"));
        Assert.Equal(0L, call[1]);
        Assert.False(Privacy.HasPendingGeckoClear);
    }

    [Fact]
    public async Task SiteDataMergesBothEnginesAndClearsOneSite()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        fixture.ChromiumPrivacy.Cookies.AddRange([new(".example.com", "a", "/"), new("www.example.com", "b", "/"), new("other.org", "c", "/")]);
        // The Public Suffix List groups shop.example.com with example.com.
        Directory.CreateDirectory(fixture.File("Blocker/Lists"));
        File.WriteAllText(fixture.File("Blocker/Lists/" + FilterListCatalog.SuffixListFileName), "com\norg\n");
        fixture.Gecko.Handlers["siteData"] = _ => JsonNode.Parse("""[{"host":"shop.example.com","cookies":2,"bytes":4096}]""");
        await fixture.Gecko.StartAsync();
        fixture.Model.NewTab("http://example.com:8080/page", EngineKind.Chromium);

        await Privacy.LoadSiteDataAsync();
        Assert.True(Privacy.SiteDataIncludesGecko);
        var example = Assert.Single(Privacy.SiteData, entry => entry.Site == "example.com");
        Assert.Equal(2, example.ChromiumCookies);
        Assert.Equal(2, example.GeckoCookies);
        Assert.Equal(4096, example.GeckoStorageBytes);

        await Privacy.ClearSiteDataAsync(example);
        var cleared = Assert.Single(fixture.ChromiumPrivacy.ClearedSites);
        Assert.Equal("example.com", cleared.Site);
        Assert.Contains("http://example.com:8080", cleared.Origins); // the open page's origin, port included
        Assert.Contains("https://shop.example.com", cleared.Origins);
        Assert.Equal("example.com", Assert.Single(fixture.Gecko.CallsTo("clearSiteData"))[0]);
    }

    private sealed class RecordingStore(string name, params BrowsingDataKind[] kinds) : IBrowsingDataClearing
    {
        public string BrowsingDataName { get; } = name;
        public IReadOnlySet<BrowsingDataKind> ClearableBrowsingData { get; } = kinds.ToHashSet();
        public List<(IReadOnlySet<BrowsingDataKind> Kinds, DateTimeOffset? Since)> Calls { get; } = [];

        public Task ClearBrowsingDataAsync(IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since, CancellationToken cancellation = default)
        {
            Calls.Add((kinds, since));
            return Task.CompletedTask;
        }
    }
}

public sealed class PrivacyAutomationTests : IDisposable
{
    private readonly PrivacyFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    private Task<AutomationReply?> Run(string command, string value = "", string? output = null) =>
        PrivacyAutomation.HandleAsync(fixture.Model, command, value, output);

    [Fact]
    public async Task OtherCommandsAreNotTaken() => Assert.Null(await Run("navigate", "example.com"));

    [Fact]
    public async Task SetChangesSettingsAndDumpShowsThem()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        foreach (var assignment in new[] { "httpsOnly=on", "strip=off", "thirdPartyCookies=off", "clearOnExit=on", "doh=strict:custom:https://dns.example/q", "httpException=legacy.example", "cosmetic=off" })
        {
            Assert.Contains("\"ok\":true", (await Run("privacy.set", assignment))!.Json);
        }
        var dump = PrivacyAutomation.Dump(fixture.Model);
        var settings = dump["settings"]!;
        Assert.True(settings["httpsOnly"]!.GetValue<bool>());
        Assert.False(settings["stripTrackingParameters"]!.GetValue<bool>());
        Assert.False(settings["blockThirdPartyCookies"]!.GetValue<bool>());
        Assert.True(settings["clearCookiesOnExit"]!.GetValue<bool>());
        Assert.Equal("strict", settings["dohMode"]!.GetValue<string>());
        Assert.Equal("https://dns.example/q", settings["dohTemplate"]!.GetValue<string>());
        Assert.Equal(["legacy.example"], settings["httpsExceptions"]!.AsArray().Select(site => site!.GetValue<string>()));
        Assert.False(dump["blocker"]!["cosmeticEnabled"]!.GetValue<bool>());

        Assert.Contains("\"ok\":true", (await Run("privacy.set", "httpException=-legacy.example"))!.Json);
        Assert.Empty(fixture.Model.Privacy.Settings.HttpsExceptions);
        Assert.Contains("\"ok\":false", (await Run("privacy.set", "bogus=1"))!.Json);
        Assert.Contains("\"ok\":false", (await Run("privacy.set", "list=nope:on"))!.Json);
    }

    [Fact]
    public async Task ListsCanBeSwitched()
    {
        Assert.Contains("\"ok\":true", (await Run("privacy.set", "list=fanboy-cookiemonster:on"))!.Json);
        Assert.Contains("fanboy-cookiemonster", fixture.Model.Blocker.EnabledListIds);
        Assert.Contains("fanboy-cookiemonster", File.ReadAllText(fixture.File("Blocker/options.json")));
        Assert.Contains("\"ok\":true", (await Run("privacy.set", "list=easylist:off"))!.Json);
        Assert.DoesNotContain("easylist", fixture.Model.Blocker.EnabledListIds);
    }

    [Fact]
    public async Task NewPrivateTabAndDumpTabs()
    {
        Assert.Contains("\"ok\":true", (await Run("privacy.newPrivateTab", "https://example.com/"))!.Json);
        var tabs = PrivacyAutomation.Dump(fixture.Model)["tabs"]!.AsArray();
        Assert.Contains(tabs, tab => tab!["isPrivate"]!.GetValue<bool>() && tab["security"]!.GetValue<string>() == "secure");
    }

    [Fact]
    public async Task ClearWritesItsSummary()
    {
        fixture.Model.StartPrivacy(fixture.ChromiumPrivacy);
        var output = fixture.File("clear.json");
        var reply = await Run("privacy.clear", "history,cookiesAndSiteData:last7Days", output);
        Assert.Contains("\"ok\":true", reply!.Json);
        Assert.Contains("last 7 days", File.ReadAllText(output));
        Assert.Contains("\"ok\":false", (await Run("privacy.clear", "passwords"))!.Json);
    }

    [Fact]
    public async Task RewriteShowsWhatALoadBecomes()
    {
        await Run("privacy.set", "httpsOnly=on");
        var reply = await Run("privacy.rewrite", "http://example.com/?utm_source=x&id=1");
        Assert.Contains("https://example.com/?id=1", reply!.Json);
    }

    [Fact]
    public async Task SettingsCommandAsksForThePageAndTheDialog()
    {
        var requests = 0;
        fixture.Model.PrivacySettingsRequested += () => requests++;
        await Run("privacy.settings", "clear");
        Assert.Equal(1, requests);
        Assert.True(fixture.Model.ShowClearBrowsingData);
    }

    [Fact]
    public void DumpIsRegisteredForEveryModel() =>
        Assert.Contains(AutomationRegistry.Handlers, handler => handler.Method.Name == nameof(PrivacyAutomation.HandleAsync));
}
