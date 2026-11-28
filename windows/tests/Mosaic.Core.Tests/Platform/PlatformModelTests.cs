using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Platform;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Platform;

/// <summary>The platform area inside the browser model: site rules, default engine, a missing Gecko runtime, languages.</summary>
public class PlatformModelTests
{
    [Fact]
    public void SiteRulesPickTheEngineForNewTabsAndTheAddressBar() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        Assert.True(model.Platform.Rules.Set("https://www.Example.com/", EngineKind.Chromium));

        var opened = model.NewTab("https://mail.example.com/inbox");
        Assert.Equal(EngineKind.Chromium, opened.Engine);
        Assert.NotNull(opened.Chromium);
        // An explicit engine wins over the rule.
        Assert.Equal(EngineKind.Gecko, model.NewTab("https://example.com/", EngineKind.Gecko).Engine);

        var tab = fixture.OpenGeckoTab("https://other.org/");
        var geckoId = tab.GeckoId!.Value;
        model.Navigate("example.com/page");
        Assert.Equal(EngineKind.Chromium, tab.Engine);
        Assert.Equal(EngineKind.Chromium, model.Engine);
        Assert.Equal("Opening in Chromium: site rule for example.com", model.Notice);
        Assert.Equal([geckoId], Assert.Single(fixture.Gecko.CallsTo("closeTab")));
        Assert.Equal(["https://example.com/page"], fixture.Chromium.Created[^1].Loads);
        model.Platform.Store.FlushAsync().Wait();
    });

    [Fact]
    public void RulesDoNotMoveAPageThatNavigatesOnItsOwn() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        fixture.Model.Platform.Rules.Set("example.com", EngineKind.Chromium);
        var tab = fixture.OpenGeckoTab("https://other.org/");
        // A link followed inside the page: Gecko reports the new address.
        fixture.Gecko.Report(Snapshots.Tab(tab.GeckoId!.Value, "https://example.com/", "Example", selected: true));
        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal("https://example.com/", tab.Url);
        await fixture.Model.Platform.Store.FlushAsync();
    });

    [Fact]
    public void RulesAreKeptBesideSettingsInTheMacOSShape() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var platform = fixture.Model.Platform;
        Assert.Equal(Path.GetDirectoryName(fixture.SettingsPath), platform.DataDirectory);
        platform.Rules.Set("youtube.com", EngineKind.Chromium, includesSubdomains: false);
        await platform.Store.FlushAsync();

        var saved = JsonNode.Parse(File.ReadAllText(fixture.PathFor("platform.json")))!;
        var rule = saved["engineRules"]!["rules"]![0]!;
        Assert.Equal("youtube.com", rule["domain"]!.GetValue<string>());
        Assert.Equal("chromium", rule["engine"]!.GetValue<string>());
        Assert.False(rule["includesSubdomains"]!.GetValue<bool>());
        Assert.Matches("^[0-9A-F-]{36}$", rule["id"]!.GetValue<string>());

        using var second = new BrowserFixture();
        File.Copy(fixture.PathFor("platform.json"), second.PathFor("platform.json"));
        Assert.Equal(EngineKind.Chromium, new EngineRuleStore(new PlatformStore(second.PathFor("platform.json"))).EngineFor("https://youtube.com/"));
    });

    [Fact]
    public void AlwaysOpenThisSiteInMovesTheTabToo() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        Assert.False(model.AddEngineRuleForCurrentSite(EngineKind.Chromium));  // the start page is not a site
        var tab = fixture.OpenGeckoTab("https://www.news.example/story");
        Assert.Equal("news.example", model.CurrentSiteDomain);

        Assert.True(model.AddEngineRuleForCurrentSite(EngineKind.Chromium));

        Assert.Equal("news.example", Assert.Single(model.Platform.Rules.Rules).Domain);
        Assert.Equal(EngineKind.Chromium, tab.Engine);
        model.Platform.Store.FlushAsync().Wait();
    });

    [Fact]
    public void StartsInChromiumWhenThereIsNoGeckoRuntime() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f => f.Gecko.Runtime = null);
        var model = fixture.Model;
        Assert.True(model.GeckoRuntimeMissing);
        Assert.False(model.IsEngineUsable(EngineKind.Gecko));
        Assert.Equal(EngineKind.Chromium, model.Engine);
        Assert.Equal(EngineKind.Chromium, model.Tabs[0].Engine);
        // Not a choice the person made: nothing is remembered.
        Assert.Null(fixture.ReloadSettings().Engine);
    });

    [Fact]
    public void AChosenEngineIsKeptEvenWithoutAGeckoRuntime() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Gecko.Runtime = null;
            File.WriteAllText(f.SettingsPath, """{"engine": "gecko"}""");
        });
        Assert.Equal(EngineKind.Gecko, fixture.Model.Engine);
    });

    [Fact]
    public void DefaultEngineIsRememberedAndABlankTabFollowsIt() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        Assert.Equal(EngineKind.Gecko, model.DefaultEngine);

        model.SetDefaultEngine(EngineKind.Chromium);

        Assert.Equal(EngineKind.Chromium, model.DefaultEngine);
        Assert.Equal(EngineKind.Chromium, model.Tabs[0].Engine);
        Assert.Equal(EngineKind.Chromium, fixture.ReloadSettings().Engine);

        fixture.Chromium.IsAvailable = false;
        model.SetDefaultEngine(EngineKind.Gecko);
        model.SetDefaultEngine(EngineKind.Chromium);
        Assert.Equal(EngineKind.Gecko, model.DefaultEngine);
    });

    [Fact]
    public void AnInstalledRuntimeReopensTheMozillaTabsThatWereWaiting() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Gecko.Runtime = null;
            f.Gecko.StartFailure = new GeckoEngineException(GeckoFailure.NoRuntime, "No Gecko runtime was found.");
        });
        var model = fixture.Model;
        var tab = model.NewTab("https://example.com/", EngineKind.Gecko);
        await TestLoop.Settle();
        Assert.Equal("No Gecko runtime was found.", tab.Failure);

        // The installer put Firefox into the profile's Engines folder.
        var engine = Directory.CreateDirectory(Path.Combine(model.Platform.DataDirectory!, "Engines", "Firefox")).FullName;
        FakePe.Write(Path.Combine(engine, "firefox.exe"), PeImage.MachineX64);
        File.WriteAllText(Path.Combine(engine, "xul.dll"), "");
        File.WriteAllText(Path.Combine(engine, "application.ini"), "[App]\nName=Firefox\nVersion=140.5.0\n[Gecko]\n");
        fixture.Gecko.StartFailure = null;

        model.PlatformEngineChanged();
        await TestLoop.Settle();

        Assert.False(model.GeckoRuntimeMissing);
        Assert.Null(tab.Failure);
        Assert.NotNull(tab.GeckoId);
        Assert.Equal(["https://example.com/", true], fixture.Gecko.CallsTo("openTab").Last());
    });

    [Fact]
    public void LanguagesGoToGeckoWhenItStartsAndWhenTheyChange() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var platform = model.Platform;
        platform.Options = new PlatformOptions { SystemLanguages = () => ["de-CH"] };
        platform.IsActive = true;
        Assert.Equal(["de-CH", "de"], platform.Languages.Preferences.Tags);
        Assert.False(platform.Languages.IsCustom);

        fixture.OpenGeckoTab("https://example.com/");
        await TestLoop.Settle();
        Assert.Equal(["de-CH,de"], fixture.Gecko.CallsTo("setAcceptLanguages").Select(call => (string)call[0]!));

        platform.Languages.Edit(list => list.Add("fr"));
        await TestLoop.Settle();
        Assert.Equal("de-CH,de,fr", (string)fixture.Gecko.CallsTo("setAcceptLanguages")[^1][0]!);
        Assert.True(platform.Languages.IsCustom);
        Assert.Equal(new ChromiumLanguageOptions("de-CH", "--accept-lang=de-CH,de,fr", "de-CH,de,fr"), platform.Languages.ChromiumOptions);
        Assert.True(platform.Languages.ChromiumNeedsRestart);

        fixture.Gecko.Handlers["setAcceptLanguages"] = _ => throw new InvalidOperationException("Gecko said no.");
        platform.Languages.Replace(new LanguagePreferences(["ja"]));
        await TestLoop.Settle();
        Assert.Equal("Gecko said no.", platform.Languages.GeckoError);

        await platform.Store.FlushAsync();
        Assert.Equal(new ChromiumLanguageOptions("ja", "--accept-lang=ja", "ja"), LanguageSettings.ChromiumOptionsFor(platform.DataDirectory!));

        platform.Languages.UseSystemLanguages();
        Assert.False(platform.Languages.IsCustom);
        Assert.Null(platform.Languages.ChromiumOptions);
        await platform.Store.FlushAsync();
    });

    [Fact]
    public void ABareModelLeavesGeckoLanguagesAlone() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        fixture.OpenGeckoTab("https://example.com/");
        await TestLoop.Settle();
        Assert.Empty(fixture.Gecko.CallsTo("setAcceptLanguages"));
        Assert.False(fixture.Model.Platform.SafeEngineSwitching);
    });

    [Fact]
    public void ExtensionToolbarButtonsOpenThePopupAsAMozillaTab() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add(TestRules.DarkReader, "Dark Reader", hasPopup: true);
            f.Library.Add("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Options Only", hasOptions: true);
            f.Library.Add("cccccccccccccccccccccccccccccccc", "Off", enabled: false);
            f.Library.Add("dddddddddddddddddddddddddddddddd", "Neither");
        });
        var model = fixture.Model;
        var items = model.ExtensionToolbarItems;
        Assert.Equal(["Dark Reader", "Neither", "Options Only"], items.Select(item => item.Name));
        Assert.Equal("Dark Reader (opens in a Mozilla tab)", items[0].Help);
        Assert.True(items[1].OpensSettings);

        Assert.True(model.PerformExtensionToolbarItem(items[0]));
        await TestLoop.Settle();
        Assert.Equal("popup", (string)fixture.Gecko.CallsTo("openExtensionPage").Single()[1]!);
        Assert.True(model.PerformExtensionToolbarItem(items[2]));
        await TestLoop.Settle();
        Assert.Equal("options", (string)fixture.Gecko.CallsTo("openExtensionPage")[^1][1]!);
        Assert.False(model.PerformExtensionToolbarItem(items[1]));
    });

    [Fact]
    public void AutomationCommandsUseTheMacOSNames() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        async Task<string> Run(string command, string value = "") =>
            (await PlatformAutomation.HandleAsync(model, command, value, null))?.Json ?? "(not a platform command)";

        Assert.Equal("""{"ok":true}""", await Run("ruleAdd", "example.com=chromium,exact"));
        Assert.False(model.Platform.Rules.Rules.Single().IncludesSubdomains);
        Assert.Contains("is not a site address", await Run("ruleAdd", "not a site=gecko"));
        Assert.Contains("Unknown engine", await Run("ruleAdd", "example.com=webkit"));
        Assert.Equal("""{"ok":true}""", await Run("ruleRemove", "https://www.example.com"));
        Assert.Empty(model.Platform.Rules.Rules);
        Assert.Contains("No rule", await Run("ruleRemove", "example.com"));

        Assert.Equal("""{"ok":true}""", await Run("defaultEngine", "chromium"));
        Assert.Equal(EngineKind.Chromium, model.DefaultEngine);
        Assert.Equal("""{"ok":true}""", await Run("languages", "en-GB,fr"));
        Assert.Equal(["en-GB", "fr"], model.Platform.Languages.Preferences.Tags);
        Assert.Contains("Expected language tags", await Run("languages", "!!"));
        Assert.Equal("""{"ok":true}""", await Run("updateFrequency", "daily"));
        Assert.Contains("found no newer release", await Run("updateDownload"));
        Assert.Contains("No update is ready", await Run("updateRestart"));
        Assert.Equal("""{"ok":true}""", await Run("safeSwitch", "on"));
        Assert.True(model.Platform.SafeEngineSwitching);
        Assert.Contains("No Firefox installer", await Run("runtimeInstall", Path.Combine(fixture.PathFor("missing"), "setup.exe")));
        Assert.Contains("There is no extension button", await Run("extensionToolbar", "0"));
        Assert.Equal("(not a platform command)", await Run("navigate", "example.com"));

        var dump = PlatformAutomation.Dump(model).AsObject();
        Assert.Equal("chromium", dump["defaultEngine"]!.GetValue<string>());
        Assert.Equal("en-GB,fr;q=0.9", dump["acceptLanguage"]!.GetValue<string>());
        Assert.True(dump["languagesCustom"]!.GetValue<bool>());
        Assert.Equal("idle", dump["runtime"]!["phase"]!.GetValue<string>());
        Assert.Equal(Path.Combine(model.Platform.DataDirectory!, "Engines", "Firefox"), dump["runtime"]!["managedPath"]!.GetValue<string>());
        Assert.Equal("daily", dump["updates"]!["frequency"]!.GetValue<string>());
        Assert.Equal(ReleaseCheck.LatestReleaseUrl.ToString(), dump["updates"]!["feed"]!.GetValue<string>());
        Assert.Equal("idle", dump["updater"]!["phase"]!.GetValue<string>());
        foreach (var key in new[] { "rules", "switching", "lastSwitch", "languages", "languageErrors", "chromiumAcceptLanguages", "extensionPermissions", "extensionToolbar", "safeSwitching" })
        {
            Assert.True(dump.ContainsKey(key), key);
        }
        await model.Platform.Store.FlushAsync();
    });
}

/// <summary>Moving a tab to the other engine keeps its page until the other engine has loaded it.</summary>
public class SafeEngineSwitchTests
{
    private static BrowserFixture Fixture()
    {
        var fixture = new BrowserFixture();
        fixture.Model.Platform.SafeEngineSwitching = true;
        return fixture;
    }

    private static JsonObject PageStatus(string document, bool loading = false, string? error = null, string title = "Example") => new()
    {
        ["loading"] = loading,
        ["url"] = document,
        ["document"] = document,
        ["title"] = title,
        ["error"] = error,
    };

    [Fact]
    public void MozillaToChromiumKeepsTheGeckoPageUntilChromiumHasLoaded() => TestLoop.Run(async () =>
    {
        using var fixture = Fixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");
        var geckoId = tab.GeckoId!.Value;

        model.SetEngine(EngineKind.Chromium);

        // Still Mozilla, with the page on screen; Chromium loads out of sight.
        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal(geckoId, tab.GeckoId);
        Assert.True(model.ShowsGeckoPage);
        Assert.Equal("Opening this page in Chromium…", model.Notice);
        Assert.Equal(EngineKind.Chromium, model.EngineSwitchTarget(tab));
        var browser = Assert.Single(fixture.Chromium.Created);
        Assert.False(browser.IsShown);
        Assert.Equal(["https://example.com/"], browser.Loads);
        Assert.Empty(fixture.Gecko.CallsTo("closeTab"));

        browser.RaiseLoadState(true);
        browser.RaiseUrl("https://example.com/");
        browser.RaiseTitle("Example Domain");
        browser.RaiseLoadState(false, canGoBack: false);
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(EngineKind.Chromium, tab.Engine);
        Assert.Same(browser, tab.Chromium);
        Assert.Null(tab.GeckoId);
        Assert.True(browser.IsShown);
        Assert.Equal(ContentKind.ChromiumPage, model.Content);
        Assert.Equal("Example Domain", tab.Title);
        Assert.False(tab.IsLoading);
        Assert.Null(model.EngineSwitchTarget(tab));
        Assert.Equal("committed to chromium", model.Platform.Switches.LastOutcome);
        Assert.Null(model.Notice);

        // The Gecko tab closes a moment later, and a late snapshot does not bring it back.
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal([geckoId], Assert.Single(fixture.Gecko.CallsTo("closeTab")));
        fixture.Gecko.Report(Snapshots.Tab(geckoId, "https://example.com/", "Example", selected: true));
        Assert.Single(model.Tabs, candidate => candidate.Url == "https://example.com/");

        // The model now follows the Chromium browser.
        browser.RaiseTitle("Changed");
        Assert.Equal("Changed", tab.Title);
    });

    [Fact]
    public void ChromiumFailingLeavesThePageInMozilla() => TestLoop.Run(async () =>
    {
        using var fixture = Fixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");

        model.SetEngine(EngineKind.Chromium);
        var browser = fixture.Chromium.Created.Single();
        browser.RaiseFailed("The Chromium engine's process ended.");
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.NotNull(tab.GeckoId);
        Assert.True(browser.IsClosed);
        Assert.Equal("Chromium could not load the page (The Chromium engine's process ended.). The page stays in Mozilla.", model.Notice);
        Assert.Equal(EngineKind.Gecko, model.Engine);
        Assert.StartsWith("failed:", model.Platform.Switches.LastOutcome);
    });

    [Fact]
    public void ASlowEngineTimesOut() => TestLoop.Run(async () =>
    {
        using var fixture = Fixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://slow.example/");
        model.SetEngine(EngineKind.Chromium);
        for (var step = 0; step < 220 && model.EngineSwitchTarget(tab) is not null; step++)
        {
            await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        }
        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal("Chromium took longer than 30 seconds to load the page. The page stays in Mozilla.", model.Notice);
        Assert.True(fixture.Chromium.Created.Single().IsClosed);
    });

    [Fact]
    public void ANewNavigationAbandonsTheSwitch() => TestLoop.Run(async () =>
    {
        using var fixture = Fixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");
        model.SetEngine(EngineKind.Chromium);
        var staging = fixture.Chromium.Created.Single();

        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(10));  // a new load gets a new timestamp
        model.Navigate("other.example");
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));

        Assert.True(staging.IsClosed);
        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal("https://other.example", tab.Url);
        Assert.Equal("abandoned", model.Platform.Switches.LastOutcome);
    });

    [Fact]
    public void SwitchingBackCancels() => TestLoop.Run(async () =>
    {
        using var fixture = Fixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");
        model.SetEngine(EngineKind.Chromium);
        model.SetEngine(EngineKind.Chromium);  // a second request for the same engine changes nothing
        Assert.Single(fixture.Chromium.Created);

        model.SetEngine(EngineKind.Gecko);
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));

        Assert.True(fixture.Chromium.Created.Single().IsClosed);
        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal(EngineKind.Gecko, model.Engine);
        Assert.Equal("cancelled", model.Platform.Switches.LastOutcome);
    });

    [Fact]
    public void ChromiumToMozillaKeepsTheChromiumPageUntilGeckoHasLoaded() => TestLoop.Run(async () =>
    {
        using var fixture = Fixture();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");
        var status = PageStatus("about:blank", loading: true);
        fixture.Gecko.Handlers["pageStatus"] = _ => status.DeepClone();
        var tabs = model.Tabs.Count;

        model.SetEngine(EngineKind.Gecko);
        await TestLoop.Settle();

        Assert.Equal(EngineKind.Chromium, tab.Engine);
        Assert.Same(browser, tab.Chromium);
        var opened = Assert.Single(fixture.Gecko.CallsTo("openTab"));
        Assert.Equal(["https://example.com/", false], opened);
        // Gecko's background tab is not mirrored as a Mosaic tab.
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com/", "", loading: true));
        Assert.Equal(tabs, model.Tabs.Count);

        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(EngineKind.Chromium, tab.Engine);  // still blank in Gecko

        status = PageStatus("https://example.com/", title: "Example Domain");
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(200));

        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal(1, tab.GeckoId);
        Assert.Null(tab.Chromium);
        Assert.Equal("Example Domain", tab.Title);
        Assert.True(model.ShowsGeckoPage);
        Assert.Equal([1], fixture.Gecko.CallsTo("selectTab").Last());
        Assert.False(browser.IsClosed);  // a moment later, so nothing flashes
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(300));
        Assert.True(browser.IsClosed);
        Assert.Equal("committed to gecko", model.Platform.Switches.LastOutcome);

        // From now on the snapshots drive the tab.
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com/next", "Next", selected: true));
        Assert.Equal("https://example.com/next", tab.Url);
    });

    [Fact]
    public void AGeckoErrorPageLeavesThePageInChromium() => TestLoop.Run(async () =>
    {
        using var fixture = Fixture();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://intranet.example/");
        fixture.Gecko.Handlers["pageStatus"] = _ => PageStatus("about:neterror?e=dnsNotFound", error: "dnsNotFound");

        model.SetEngine(EngineKind.Gecko);
        await TestLoop.Settle();
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(200));

        Assert.Equal(EngineKind.Chromium, tab.Engine);
        Assert.False(browser.IsClosed);
        Assert.Equal([1], fixture.Gecko.CallsTo("closeTab").Single());
        Assert.Equal("Mozilla could not load the page (dnsNotFound). The page stays in Chromium.", model.Notice);
        Assert.Equal(EngineKind.Chromium, model.Engine);
    });

    [Fact]
    public void WithoutAGeckoRuntimeTheSwitchFailsAtOnce() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Gecko.Runtime = null);
        fixture.Model.Platform.SafeEngineSwitching = true;
        var (tab, _) = fixture.OpenChromiumTab("https://example.com/");
        fixture.Model.SetEngine(EngineKind.Gecko);
        await TestLoop.Settle();
        Assert.Equal(EngineKind.Chromium, tab.Engine);
        Assert.Equal("The Mozilla engine is not installed. Install it in Settings › Engine. The page stays in Chromium.", fixture.Model.Notice);
        Assert.Empty(fixture.Gecko.CallsTo("openTab"));
    });

    [Fact]
    public void BlankAndFailedTabsSwitchDirectly() => TestLoop.Run(() =>
    {
        using var fixture = Fixture();
        var model = fixture.Model;
        model.SetEngine(EngineKind.Chromium);
        Assert.Equal(EngineKind.Chromium, model.Tabs[0].Engine);
        Assert.Null(model.Platform.Switches.LastOutcome);
    });

    [Fact]
    public void SameDocumentIgnoresTheFragment()
    {
        Assert.True(BrowserModel.SameDocument("https://a.example/x#one", "https://a.example/x#two"));
        Assert.False(BrowserModel.SameDocument("https://a.example/x", "https://a.example/y"));
    }
}
