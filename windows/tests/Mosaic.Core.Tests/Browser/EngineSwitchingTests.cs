using Mosaic.Core.Browser;
using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Browser;

public class EngineSwitchingTests
{
    [Fact]
    public void SwitchMovesTheTabToChromiumAndReloadsItThere() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com/", "Example", canGoBack: true, selected: true));
        Assert.True(model.CanGoBack);

        model.SetEngine(EngineKind.Chromium);

        Assert.Equal(EngineKind.Chromium, tab.Engine);
        Assert.Equal(EngineKind.Chromium, model.Engine);
        Assert.Null(tab.GeckoId);
        Assert.Equal([1], Assert.Single(fixture.Gecko.CallsTo("closeTab")));
        var browser = Assert.Single(fixture.Chromium.Created);
        Assert.Same(browser, tab.Chromium);
        Assert.Equal(["https://example.com/"], browser.Loads);
        Assert.True(browser.IsShown);
        // History does not travel between engines.
        Assert.False(model.CanGoBack);
        Assert.True(tab.IsLoading);
        Assert.Equal(ContentKind.ChromiumPage, model.Content);
        Assert.False(model.ShowsGeckoPage);

        // The Gecko tab is gone for good, whatever a snapshot already on its way says.
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com/", "Example", selected: true));
        Assert.Equal(2, model.Tabs.Count);
        Assert.Equal(EngineKind.Chromium, tab.Engine);
    });

    [Fact]
    public void SwitchMovesTheTabBackToGecko() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");
        browser.RaiseTitle("Example");

        model.SetEngine(EngineKind.Gecko);

        Assert.True(browser.IsClosed);
        Assert.Null(tab.Chromium);
        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal(["https://example.com/", true], Assert.Single(fixture.Gecko.CallsTo("openTab")));
        Assert.Equal(1, tab.GeckoId);
        Assert.True(model.ShowsGeckoPage);

        // The browser the tab let go of no longer speaks for it.
        browser.RaiseTitle("Stale");
        browser.RaiseUrl("https://stale.example/");
        Assert.Equal("", tab.Title);
        Assert.Equal("https://example.com/", tab.Url);
    });

    [Fact]
    public void SwitchingABlankTabChangesItsEngineWithoutLoadingAnything() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;

        model.SetEngine(EngineKind.Chromium);

        Assert.Equal(EngineKind.Chromium, model.Tabs[0].Engine);
        Assert.Empty(fixture.Chromium.Created);
        Assert.True(model.ShowsStartPage);
        Assert.Equal("New tabs open in Chromium · searching with Google", model.StartPageCaption);
    });

    [Fact]
    public void SwitchingToTheEngineTheTabIsAlreadyOnChangesNothing() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var tab = fixture.OpenGeckoTab("https://example.com/");
        fixture.Gecko.Calls.Clear();

        fixture.Model.SetEngine(EngineKind.Gecko);

        Assert.Equal(1, tab.GeckoId);
        Assert.Empty(fixture.Gecko.Calls);
    });

    [Fact]
    public void ToggleFlipsTheSelectedTabsEngine() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;

        model.ToggleEngine();
        Assert.Equal(EngineKind.Chromium, model.SelectedTab!.Engine);
        model.ToggleEngine();
        Assert.Equal(EngineKind.Gecko, model.SelectedTab!.Engine);
    });

    [Fact]
    public void WithoutChromiumTheSwitchExplainsAndChangesNothing() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Chromium.IsAvailable = false;
            f.Chromium.UnavailableReason = "Install the WebView2 Runtime.";
        });
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");

        model.SetEngine(EngineKind.Chromium);

        Assert.Equal("Install the WebView2 Runtime.", model.Notice);
        Assert.True(model.HasNotice);
        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal(EngineKind.Gecko, model.Engine);
        Assert.Equal(1, tab.GeckoId);
        Assert.Null(fixture.ReloadSettings().Engine);
    });

    [Fact]
    public void EngineFollowsTheSelectedTabButOnlyAnExplicitSwitchIsRemembered() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var geckoTab = model.Tabs[0];

        // Opening and selecting tabs moves the engine for new tabs around…
        var (chromiumTab, _) = fixture.OpenChromiumTab("https://example.com/");
        Assert.Equal(EngineKind.Chromium, model.Engine);
        Assert.Null(fixture.ReloadSettings().Engine);

        // …and the switch is what gets written down.
        model.SetEngine(EngineKind.Gecko);
        Assert.Equal(EngineKind.Gecko, fixture.ReloadSettings().Engine);
        model.SetEngine(EngineKind.Chromium);
        Assert.Equal(EngineKind.Chromium, fixture.ReloadSettings().Engine);

        model.Select(geckoTab);
        Assert.Equal(EngineKind.Gecko, model.Engine);
        Assert.Equal(EngineKind.Chromium, fixture.ReloadSettings().Engine);
        Assert.Equal(EngineKind.Chromium, chromiumTab.Engine);
    });

    [Fact]
    public void NextLaunchStartsWithTheRememberedEngine() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
            new BrowserSettings(f.SettingsPath) { Engine = EngineKind.Chromium }.Save());

        Assert.Equal(EngineKind.Chromium, fixture.Model.Engine);
        Assert.Equal(EngineKind.Chromium, fixture.Model.Tabs[0].Engine);
    });

    [Fact]
    public void RememberedChromiumFallsBackToGeckoWhenChromiumIsUnavailable() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            new BrowserSettings(f.SettingsPath) { Engine = EngineKind.Chromium }.Save();
            f.Chromium.IsAvailable = false;
        });

        Assert.Equal(EngineKind.Gecko, fixture.Model.Engine);
        Assert.Equal(EngineKind.Gecko, fixture.Model.Tabs[0].Engine);
        // The preference itself survives for when the runtime is installed.
        Assert.Equal(EngineKind.Chromium, fixture.ReloadSettings().Engine);
    });

    [Fact]
    public void SearchEngineChoiceIsRememberedAndShownInThePlaceholder() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        Assert.Equal(SearchEngine.Google, model.SearchEngine);
        Assert.Equal("Search Google or enter address", model.AddressPlaceholder);

        model.SearchEngine = SearchEngine.Kagi;

        Assert.Equal("Search Kagi or enter address", model.AddressPlaceholder);
        Assert.Equal("New tabs open in Mozilla · searching with Kagi", model.StartPageCaption);
        Assert.Equal("kagi", fixture.ReloadSettings().SearchEngineId);
    });

    [Fact]
    public void NextLaunchStartsWithTheRememberedSearchEngine() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
            new BrowserSettings(f.SettingsPath) { SearchEngineId = "ecosia" }.Save());

        Assert.Equal(SearchEngine.Ecosia, fixture.Model.SearchEngine);
    });

    [Fact]
    public void UnknownRememberedSearchEngineFallsBackToTheDefault() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
            new BrowserSettings(f.SettingsPath) { SearchEngineId = "altavista" }.Save());

        Assert.Equal(SearchEngine.Default, fixture.Model.SearchEngine);
    });

    [Fact]
    public void CyclingTheSearchEngineAnnouncesAndRemembersIt() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;

        model.CycleSearchEngine();

        Assert.Equal(SearchEngine.DuckDuckGo, model.SearchEngine);
        Assert.Equal("Search engine: DuckDuckGo", model.Notice);
        Assert.Equal("duckduckgo", fixture.ReloadSettings().SearchEngineId);
    });

    [Fact]
    public void NoticeClearsItselfAndANewerNoticeGetsItsFullTime() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;

        model.Notice = "first";
        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("first", model.Notice);

        model.Notice = "second";
        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(1));
        // The first notice's timer has run out; it must not take the second one with it.
        Assert.Equal("second", model.Notice);

        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        Assert.Null(model.Notice);
        Assert.False(model.HasNotice);
    });

    [Fact]
    public void ModelAnnouncesTheChangesTheWindowBindsTo() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.Navigate("https://example.com/");
        model.SetEngine(EngineKind.Chromium);
        model.CycleSearchEngine();
        model.ShowExtensions = true;

        string[] expected =
        [
            nameof(BrowserModel.AddressText), nameof(BrowserModel.AddressKind), nameof(BrowserModel.IsLoading),
            nameof(BrowserModel.CanReload), nameof(BrowserModel.Content), nameof(BrowserModel.ShowsStartPage),
            nameof(BrowserModel.ShowsGeckoPage), nameof(BrowserModel.Engine), nameof(BrowserModel.IsGeckoEngine),
            nameof(BrowserModel.IsChromiumEngine), nameof(BrowserModel.SearchEngine),
            nameof(BrowserModel.AddressPlaceholder), nameof(BrowserModel.StartPageCaption),
            nameof(BrowserModel.Notice), nameof(BrowserModel.HasNotice), nameof(BrowserModel.ShowExtensions),
        ];
        Assert.All(expected, name => Assert.Contains(name, changed));
    });
}
