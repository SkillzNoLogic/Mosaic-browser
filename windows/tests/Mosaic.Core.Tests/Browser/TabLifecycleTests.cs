using Mosaic.Core.Browser;

namespace Mosaic.Core.Tests.Browser;

public class TabLifecycleTests
{
    [Fact]
    public void StartsWithOneBlankMozillaTab() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;

        var tab = Assert.Single(model.Tabs);
        Assert.Same(tab, model.SelectedTab);
        Assert.True(tab.IsSelected);
        Assert.Equal(EngineKind.Gecko, tab.Engine);
        Assert.Equal(EngineKind.Gecko, model.Engine);
        Assert.True(tab.IsStartPage);
        Assert.Equal("New Tab", tab.DisplayTitle);
        Assert.Equal(ContentKind.StartPage, model.Content);
        Assert.True(model.ShowsStartPage);
        Assert.False(model.ShowsGeckoPage);
        Assert.False(model.ShowsMessage);
        Assert.Equal("", model.AddressText);
        // Nothing has been loaded, so Gecko has not been started.
        Assert.Equal(0, fixture.Gecko.StartCount);
    });

    [Fact]
    public void NewTabOpensNextToTheSelectedTabAndIsSelected() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var first = model.Tabs[0];
        var last = model.NewTab();

        model.Select(first);
        var middle = model.NewTab();

        Assert.Equal([first, middle, last], model.Tabs);
        Assert.Same(middle, model.SelectedTab);
        Assert.True(middle.IsSelected);
        Assert.False(first.IsSelected);
    });

    [Fact]
    public void BlankNewTabAsksForAddressFocus() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();

        fixture.Model.NewTab();
        Assert.Equal(1, fixture.AddressFocusRequests);

        // A tab that opens with a page leaves focus alone.
        fixture.Model.NewTab("https://example.com/");
        Assert.Equal(1, fixture.AddressFocusRequests);
        // Activating the window is only ever asked for on behalf of the Gecko window.
        Assert.Equal(0, fixture.WindowActivationRequests);
    });

    [Fact]
    public void BackgroundTabDoesNotTakeSelectionOrFocus() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var first = model.Tabs[0];

        var background = model.NewTab(select: false);

        Assert.Same(first, model.SelectedTab);
        Assert.False(background.IsSelected);
        Assert.Equal([first, background], model.Tabs);
        Assert.Equal(0, fixture.AddressFocusRequests);
    });

    [Fact]
    public void NewTabUsesTheEngineOfTheTabYouAreOn() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (chromiumTab, _) = fixture.OpenChromiumTab("https://example.com/");
        Assert.Equal(EngineKind.Chromium, model.Engine);

        Assert.Equal(EngineKind.Chromium, model.NewTab().Engine);

        model.Select(model.Tabs[0]);
        Assert.Equal(EngineKind.Gecko, model.Engine);
        Assert.Equal(EngineKind.Gecko, model.NewTab().Engine);

        model.Select(chromiumTab);
        Assert.True(model.IsChromiumEngine);
        Assert.False(model.IsGeckoEngine);
    });

    [Fact]
    public void ClosingTheSelectedTabSelectsTheOneThatTakesItsPlace() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var a = model.Tabs[0];
        var b = model.NewTab();
        var c = model.NewTab();
        Assert.Equal([a, b, c], model.Tabs);

        model.Select(b);
        model.Close(b);
        Assert.Equal([a, c], model.Tabs);
        Assert.Same(c, model.SelectedTab);
        Assert.False(b.IsSelected);

        // Closing the last tab in the strip falls back to the one before it.
        model.Close(c);
        Assert.Same(a, model.SelectedTab);
        Assert.True(a.IsSelected);
    });

    [Fact]
    public void ClosingABackgroundTabKeepsTheSelection() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var a = model.Tabs[0];
        var b = model.NewTab();

        model.Close(a);

        Assert.Equal([b], model.Tabs);
        Assert.Same(b, model.SelectedTab);
    });

    [Fact]
    public void ClosingTheOnlyTabOpensAFreshOne() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var only = model.Tabs[0];

        model.CloseSelectedTab();

        var fresh = Assert.Single(model.Tabs);
        Assert.NotSame(only, fresh);
        Assert.Same(fresh, model.SelectedTab);
        Assert.True(fresh.IsStartPage);
    });

    [Fact]
    public void ClosingATabThatIsNotInTheStripDoesNothing() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var closed = model.NewTab();
        model.Close(closed);
        var before = model.Tabs.ToList();

        model.Close(closed);
        model.Select(closed);

        Assert.Equal(before, model.Tabs);
        Assert.Same(before[0], model.SelectedTab);
    });

    [Fact]
    public void AdjacentTabSelectionWrapsAround() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var a = model.Tabs[0];
        var b = model.NewTab();
        var c = model.NewTab();

        model.SelectAdjacentTab(1);
        Assert.Same(a, model.SelectedTab);
        model.SelectAdjacentTab(-1);
        Assert.Same(c, model.SelectedTab);
        model.SelectAdjacentTab(-1);
        Assert.Same(b, model.SelectedTab);
    });

    [Fact]
    public void SelectingATabShowsItsAddressAndTellsGecko() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var page = fixture.OpenGeckoTab("https://example.com/");
        var blank = model.NewTab();
        Assert.Equal("", model.AddressText);
        fixture.Gecko.Calls.Clear();

        model.Select(page);

        Assert.Equal("https://example.com/", model.AddressText);
        Assert.Equal([page.GeckoId], Assert.Single(fixture.Gecko.CallsTo("selectTab")));

        // A tab Gecko does not have yet is nothing to tell Gecko about.
        fixture.Gecko.Calls.Clear();
        model.Select(blank);
        Assert.Empty(fixture.Gecko.Calls);
    });

    [Fact]
    public void ClosingATabReleasesItsEngineResources() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var geckoTab = fixture.OpenGeckoTab("https://example.com/");
        var geckoId = geckoTab.GeckoId;
        var (chromiumTab, browser) = fixture.OpenChromiumTab("https://example.org/");

        model.Close(geckoTab);
        model.Close(chromiumTab);

        Assert.Equal([geckoId], Assert.Single(fixture.Gecko.CallsTo("closeTab")));
        Assert.Null(geckoTab.GeckoId);
        Assert.True(browser.IsClosed);
        Assert.Null(chromiumTab.Chromium);
    });

    [Fact]
    public void TabLabelFallsBackFromTitleToHostToAddress() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var (tab, browser) = fixture.OpenChromiumTab("https://www.example.com/path");
        Assert.Equal("www.example.com", tab.DisplayTitle);

        browser.RaiseTitle("Example Domain");
        Assert.Equal("Example Domain", tab.DisplayTitle);
        Assert.Equal("Example Domain, Chromium", tab.AccessibleName);
        Assert.Equal("Rendered by Chromium (Blink)", tab.EngineHint);

        var (odd, _) = fixture.OpenChromiumTab("about:blank");
        Assert.Equal("about:blank", odd.DisplayTitle);
    });

    [Fact]
    public void TabAnnouncesWhatTheStripShows() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        browser.RaiseTitle("Example");
        browser.RaiseLoadState(loading: false);

        Assert.Contains(nameof(BrowserTab.Title), changed);
        Assert.Contains(nameof(BrowserTab.DisplayTitle), changed);
        Assert.Contains(nameof(BrowserTab.AccessibleName), changed);
        Assert.Contains(nameof(BrowserTab.IsLoading), changed);
    });
}
