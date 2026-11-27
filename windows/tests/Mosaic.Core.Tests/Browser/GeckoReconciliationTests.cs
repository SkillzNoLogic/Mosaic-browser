using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using static Mosaic.Core.Tests.Browser.Snapshots;

namespace Mosaic.Core.Tests.Browser;

/// <summary>
/// How Mosaic's tab list follows what Gecko reports. Each test is a rule that was learned the
/// hard way on macOS and has to hold here too.
/// </summary>
public class GeckoReconciliationTests
{
    private static readonly TimeSpan PastSelectionGrace = TimeSpan.FromSeconds(1);

    [Fact]
    public void SnapshotUpdatesTheTabAndTheToolbar() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");

        fixture.Gecko.Report(Tab(1, "https://www.example.com/home", "Example", loading: false, canGoBack: true,
            canGoForward: true, selected: true));

        Assert.Equal("https://www.example.com/home", tab.Url);
        Assert.Equal("Example", tab.Title);
        Assert.False(tab.IsLoading);
        Assert.Equal("https://www.example.com/home", model.AddressText);
        Assert.True(model.CanGoBack);
        Assert.True(model.CanGoForward);
        Assert.False(model.IsLoading);
    });

    [Fact]
    public void BlankPageDoesNotFlashIntoTheAddressBarWhileALoadSettles() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");

        // Gecko has created the tab but not left the blank page it starts from.
        fixture.Gecko.Report(Tab(1, "about:blank", loading: true, selected: true));
        Assert.Equal("https://example.com/", tab.Url);
        Assert.Equal("https://example.com/", model.AddressText);
        Assert.True(tab.IsLoading);

        // Not loading yet either, but the request is recent: still the stale blank page.
        fixture.Gecko.Report(Tab(1, "about:blank", loading: false, selected: true));
        Assert.Equal("https://example.com/", tab.Url);
        Assert.True(tab.IsLoading);

        fixture.Gecko.Report(Tab(1, "https://example.com/", "Example", loading: false, selected: true));
        Assert.Equal("Example", tab.Title);
        Assert.False(tab.IsLoading);

        // Long after the request, a blank page is simply where the tab is.
        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(2));
        fixture.Gecko.Report(Tab(1, "about:blank", loading: false, selected: true));
        Assert.Equal("about:blank", tab.Url);
        Assert.Equal("about:blank", model.AddressText);
    });

    [Fact]
    public void TabOpenedByALinkIsAdoptedNextToItsOpenerAndShown() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var opener = fixture.OpenGeckoTab("https://example.com/");
        var trailing = model.NewTab(select: false);
        fixture.Gecko.Report(Tab(1, "https://example.com/", selected: true));
        fixture.Gecko.Calls.Clear();

        fixture.Gecko.Report(
            Tab(1, "https://example.com/"),
            Tab(2, "https://popup.example/", "Popup", loading: true, selected: true));

        Assert.Equal(4, model.Tabs.Count);
        var adopted = model.Tabs[model.Tabs.IndexOf(opener) + 1];
        Assert.NotSame(trailing, adopted);
        Assert.Equal(EngineKind.Gecko, adopted.Engine);
        Assert.Equal(2, adopted.GeckoId);
        Assert.Equal("https://popup.example/", adopted.Url);
        Assert.Equal("Popup", adopted.Title);
        Assert.True(adopted.IsLoading);
        Assert.Same(adopted, model.SelectedTab);
        Assert.Equal("https://popup.example/", model.AddressText);
        Assert.True(model.ShowsGeckoPage);
        // Gecko already made it current: nothing is sent back.
        Assert.Empty(fixture.Gecko.Calls);
    });

    [Fact]
    public void TabGeckoOpensOnItsOwnStaysInTheBackground() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var page = fixture.OpenGeckoTab("https://example.com/");

        // Not selected in Gecko: an extension's welcome page opened behind the current tab.
        fixture.Gecko.Report(Tab(1, "https://example.com/", selected: true), Tab(2, "moz-extension://abc/welcome.html"));
        Assert.Same(page, model.SelectedTab);
        Assert.Contains(model.Tabs, tab => tab.GeckoId == 2);

        // Selected in Gecko, but the user is on a blank tab: it must not be yanked away.
        var blank = model.NewTab();
        fixture.Gecko.Report(
            Tab(1, "https://example.com/"), Tab(2, "moz-extension://abc/welcome.html"),
            Tab(3, "https://other.example/", selected: true));
        Assert.Same(blank, model.SelectedTab);
        Assert.Contains(model.Tabs, tab => tab.GeckoId == 3);

        // The same goes for a Chromium tab.
        var (chromiumTab, _) = fixture.OpenChromiumTab("https://example.org/");
        fixture.Gecko.Report(
            Tab(1, "https://example.com/"), Tab(2, "moz-extension://abc/welcome.html"),
            Tab(3, "https://other.example/"), Tab(4, "https://fourth.example/", selected: true));
        Assert.Same(chromiumTab, model.SelectedTab);
        Assert.Contains(model.Tabs, tab => tab.GeckoId == 4);
    });

    [Fact]
    public void TabMosaicIsOpeningIsNotAdoptedASecondTime() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        fixture.Gecko.Hold("openTab");
        var tab = model.NewTab("https://example.com/");

        // Gecko has the tab, but the reply telling Mosaic its ID has not arrived.
        fixture.Gecko.Report(Tab(1, "about:blank", loading: true, selected: true));
        Assert.Equal(2, model.Tabs.Count);

        fixture.Gecko.Release("openTab");
        fixture.Gecko.Report(Tab(1, "https://example.com/", "Example", selected: true));

        Assert.Equal(2, model.Tabs.Count);
        Assert.Equal(1, tab.GeckoId);
        Assert.Equal("Example", tab.Title);
    });

    [Fact]
    public void TabClosedByItsPageIsClosedHereWithoutTellingGecko() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var first = fixture.OpenGeckoTab("https://example.com/");
        var second = fixture.OpenGeckoTab("https://example.org/");
        fixture.Gecko.Calls.Clear();

        fixture.Gecko.Report(Tab(1, "https://example.com/", selected: true));

        Assert.DoesNotContain(second, model.Tabs);
        Assert.Same(first, model.SelectedTab);
        Assert.Empty(fixture.Gecko.CallsTo("closeTab"));
    });

    [Fact]
    public void MissingTabsAreNotClosedWhileAnOpenIsInFlight() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var first = fixture.OpenGeckoTab("https://example.com/");
        fixture.Gecko.Hold("openTab");
        model.NewTab("https://example.org/");

        // A snapshot from the middle of the open may be from before or after anything.
        fixture.Gecko.Report();

        Assert.Contains(first, model.Tabs);
        Assert.Equal(1, first.GeckoId);
    });

    [Fact]
    public void JustClosedTabIsNotResurrectedByAStaleSnapshot() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var keep = fixture.OpenGeckoTab("https://example.com/");
        var close = fixture.OpenGeckoTab("https://example.org/");

        model.Close(close);
        // Taken just before the close landed in Gecko.
        fixture.Gecko.Report(Tab(1, "https://example.com/"), Tab(2, "https://example.org/", selected: true));

        Assert.Equal(2, model.Tabs.Count);
        Assert.DoesNotContain(model.Tabs, tab => tab.GeckoId == 2);
        Assert.Same(keep, model.SelectedTab);
    });

    [Fact]
    public void ClosedTabIdsAreForgottenWhenGeckoRestartsAndCountsAgain() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");
        model.Close(tab);

        fixture.Gecko.Crash();
        model.Navigate("https://example.org/");
        // A fresh Gecko numbers its tabs from the start; ID 1 now means something else.
        fixture.Gecko.Report(Tab(1, "https://unrelated.example/"), Tab(2, "https://example.org/", selected: true));

        Assert.Contains(model.Tabs, candidate => candidate.GeckoId == 1 && candidate.Url == "https://unrelated.example/");
    });

    [Fact]
    public void SelectionMadeInsideGeckoIsFollowedOnlyWhenItChanges() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var first = fixture.OpenGeckoTab("https://example.com/");
        var second = fixture.OpenGeckoTab("https://example.org/");
        await fixture.Time.AdvanceAsync(PastSelectionGrace);

        // The first report only establishes what Gecko considers selected. Here it disagrees
        // with Mosaic (which has `second`), and that alone must not move anything.
        fixture.Gecko.Report(Tab(1, "https://example.com/", selected: true), Tab(2, "https://example.org/"));
        Assert.Same(second, model.SelectedTab);
        fixture.Gecko.Report(Tab(1, "https://example.com/", selected: true), Tab(2, "https://example.org/"));
        Assert.Same(second, model.SelectedTab);

        // Gecko switches (a page focused its opener): follow it, without echoing the switch back.
        fixture.Gecko.Report(Tab(1, "https://example.com/"), Tab(2, "https://example.org/", selected: true));
        Assert.Same(second, model.SelectedTab);
        fixture.Gecko.Calls.Clear();
        fixture.Gecko.Report(Tab(1, "https://example.com/", selected: true), Tab(2, "https://example.org/"));

        Assert.Same(first, model.SelectedTab);
        Assert.Equal("https://example.com/", model.AddressText);
        Assert.Empty(fixture.Gecko.CallsTo("selectTab"));
    });

    [Fact]
    public void SelectionChangeInGeckoRightAfterALocalSelectionIsIgnored() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var first = fixture.OpenGeckoTab("https://example.com/");
        var second = fixture.OpenGeckoTab("https://example.org/");
        await fixture.Time.AdvanceAsync(PastSelectionGrace);
        fixture.Gecko.Report(Tab(1, "https://example.com/"), Tab(2, "https://example.org/", selected: true));

        // The user clicks the first tab; a snapshot from before Gecko caught up says otherwise.
        model.Select(first);
        fixture.Gecko.Report(Tab(1, "https://example.com/", selected: true), Tab(2, "https://example.org/"));
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(300));
        fixture.Gecko.Report(Tab(1, "https://example.com/"), Tab(2, "https://example.org/", selected: true));

        Assert.Same(first, model.SelectedTab);
        Assert.NotSame(second, model.SelectedTab);
    });

    [Fact]
    public void SelectionChangeInGeckoDoesNotPullTheUserOffABlankOrChromiumTab() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        fixture.OpenGeckoTab("https://example.com/");
        fixture.OpenGeckoTab("https://example.org/");
        fixture.Gecko.Report(Tab(1, "https://example.com/"), Tab(2, "https://example.org/", selected: true));

        var blank = model.NewTab();
        await fixture.Time.AdvanceAsync(PastSelectionGrace);
        fixture.Gecko.Report(Tab(1, "https://example.com/", selected: true), Tab(2, "https://example.org/"));
        Assert.Same(blank, model.SelectedTab);

        var (chromiumTab, _) = fixture.OpenChromiumTab("https://example.net/");
        await fixture.Time.AdvanceAsync(PastSelectionGrace);
        fixture.Gecko.Report(Tab(1, "https://example.com/"), Tab(2, "https://example.org/", selected: true));
        Assert.Same(chromiumTab, model.SelectedTab);
    });

    [Fact]
    public void ShortcutsPressedInsideGeckoArriveAsEvents() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var page = fixture.OpenGeckoTab("https://example.com/");
        GeckoTabState[] tabs = [Tab(1, "https://example.com/", selected: true)];

        fixture.Gecko.ReportEvents(tabs, "focusAddress");
        Assert.Equal(1, fixture.AddressFocusRequests);
        Assert.Equal(1, fixture.WindowActivationRequests);

        fixture.Gecko.ReportEvents(tabs, "newTab");
        Assert.Equal(3, model.Tabs.Count);
        Assert.True(model.SelectedTab!.IsStartPage);
        Assert.Equal(2, fixture.WindowActivationRequests);
        Assert.Equal(2, fixture.AddressFocusRequests);

        fixture.Gecko.ReportEvents(tabs, "closeTab");
        Assert.Equal(2, model.Tabs.Count);
        Assert.Same(page, model.SelectedTab);

        fixture.Gecko.ReportEvents(tabs, "toggleEngine", "somethingNew");
        Assert.Equal(EngineKind.Chromium, page.Engine);
        Assert.Single(fixture.Chromium.Created);
    });

    [Fact]
    public void EventsFromGeckoNeverActivateTheWindowWhenFocusChangesAreOff() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        fixture.Model.AllowsFocusChanges = false;
        fixture.OpenGeckoTab("https://example.com/");
        GeckoTabState[] tabs = [Tab(1, "https://example.com/", selected: true)];

        fixture.Gecko.ReportEvents(tabs, "focusAddress", "newTab");

        Assert.Equal(0, fixture.WindowActivationRequests);
        // Focus inside Mosaic's own window is still put where it belongs.
        Assert.Equal(2, fixture.AddressFocusRequests);
        Assert.Equal(3, fixture.Model.Tabs.Count);
    });

    [Fact]
    public void WhenGeckoStopsItsTabsSaySoAndTheStartPageIsLeftAlone() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var blank = model.Tabs[0];
        var page = fixture.OpenGeckoTab("https://example.com/");
        var (chromiumTab, browser) = fixture.OpenChromiumTab("https://example.org/");
        model.Select(page);
        Assert.True(model.ShowsGeckoPage);

        fixture.Gecko.Crash();

        Assert.Null(page.GeckoId);
        Assert.False(page.IsLoading);
        Assert.Equal("Gecko stopped. Reload to start it again.", page.Failure);
        Assert.Null(blank.Failure);
        Assert.Null(chromiumTab.Failure);
        Assert.False(browser.IsClosed);
        Assert.False(model.ShowsGeckoPage);
        Assert.Equal(ContentKind.Failure, model.Content);
        Assert.Equal("Gecko stopped. Reload to start it again.", model.Message!.Detail);
    });

    [Fact]
    public void GeckoStoppingMidOpenLeavesTheStoppedMessageNotARawError() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        fixture.Gecko.Hold("openTab");
        var tab = fixture.Model.NewTab("https://example.com/");

        fixture.Gecko.Crash();
        fixture.Gecko.Fail("openTab", new InvalidOperationException("connection closed"));

        Assert.Equal("Gecko stopped. Reload to start it again.", tab.Failure);
        Assert.Null(tab.GeckoId);
    });

    [Fact]
    public void GeckoPageShowsOnlyForALiveGeckoTab() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f => f.Gecko.StartGate = new TaskCompletionSource());
        var model = fixture.Model;
        var changes = 0;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserModel.ShowsGeckoPage)) changes++;
        };

        Assert.False(model.ShowsGeckoPage);
        model.Navigate("https://example.com/");
        Assert.False(model.ShowsGeckoPage);

        fixture.Gecko.StartGate!.SetResult();
        Assert.True(model.ShowsGeckoPage);
        Assert.Null(model.Message);
        Assert.True(changes > 0);

        var blank = model.NewTab();
        Assert.False(model.ShowsGeckoPage);
        model.Close(blank);
        Assert.True(model.ShowsGeckoPage);

        model.SetEngine(EngineKind.Chromium);
        Assert.False(model.ShowsGeckoPage);
        model.SetEngine(EngineKind.Gecko);
        Assert.True(model.ShowsGeckoPage);

        fixture.Gecko.Crash();
        Assert.False(model.ShowsGeckoPage);
    });

    [Fact]
    public void EnabledExtensionsAreQuietlyReinstalledEveryTimeGeckoStarts() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            f.Library.Add("bbbb", "Beta", enabled: false);
        });
        var model = fixture.Model;

        model.Navigate("https://example.com/");

        // Quiet (second argument): the extension must not open its welcome page again.
        Assert.Equal(["/library/aaaa/gecko", true], Assert.Single(fixture.Gecko.CallsTo("installExtension")));
        // Extensions go in before the page is opened, so they apply to it.
        Assert.Equal(["installExtension", "openTab"], fixture.Gecko.Calls.Select(call => call.Method));
        Assert.True(fixture.Extensions.IsLoaded(fixture.Extensions.Extensions[0]));

        fixture.Gecko.Crash();
        Assert.False(fixture.Extensions.IsLoaded(fixture.Extensions.Extensions[0]));
        model.Reload();

        Assert.True(fixture.Extensions.IsLoaded(fixture.Extensions.Extensions[0]));
        Assert.Equal(2, fixture.Gecko.CallsTo("installExtension").Count);
        Assert.All(fixture.Gecko.CallsTo("installExtension"), arguments => Assert.Equal(true, arguments[1]));
    });

    [Fact]
    public void ExtensionPageOpensInAGeckoTabThatIsSelectedOnceAdopted() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add("aaaa", "Alpha", hasPopup: true));
        var model = fixture.Model;
        var extension = fixture.Extensions.Extensions[0];
        model.ShowExtensions = true;

        await model.OpenExtensionPageAsync(extension, "popup");

        Assert.Equal(1, fixture.Gecko.StartCount);
        Assert.Equal(["aaaa@crx.mosaic", "popup"], Assert.Single(fixture.Gecko.CallsTo("openExtensionPage")));
        Assert.False(model.ShowExtensions);

        // The user is on the blank tab, where adopted tabs normally stay in the background;
        // this one they asked for.
        Assert.True(model.SelectedTab!.IsStartPage);
        fixture.Gecko.Report(Tab(1, "moz-extension://abc/popup.html", "Alpha", selected: true));

        Assert.Equal(1, model.SelectedTab!.GeckoId);
        Assert.Equal("moz-extension://abc/popup.html", model.AddressText);
        Assert.True(model.ShowsGeckoPage);
    });

    [Fact]
    public void ExtensionPageLoadsTheExtensionFirstIfGeckoDoesNotHaveIt() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add("aaaa", "Alpha", enabled: false));
        var extension = fixture.Extensions.Extensions[0];

        await fixture.Model.OpenExtensionPageAsync(extension, "options");

        Assert.Equal(["installExtension", "openExtensionPage"], fixture.Gecko.Calls.Select(call => call.Method));
        Assert.Equal(["/library/aaaa/gecko", false], fixture.Gecko.CallsTo("installExtension")[0]);
    });

    [Fact]
    public void ExtensionWithoutThePageSaysSo() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add("aaaa", "Alpha"));
        fixture.Gecko.Handlers["openExtensionPage"] = _ => null;
        fixture.Model.ShowExtensions = true;

        await fixture.Model.OpenExtensionPageAsync(fixture.Extensions.Extensions[0], "options");

        Assert.Equal("Alpha has no options page.", fixture.Model.Notice);
        Assert.True(fixture.Model.ShowExtensions);
    });

    [Fact]
    public void ExtensionPageFailureBecomesANotice() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            f.Gecko.StartFailure = new InvalidOperationException("No Gecko runtime found.");
        });

        await fixture.Model.OpenExtensionPageAsync(fixture.Extensions.Extensions[0], "popup");

        Assert.Equal("No Gecko runtime found.", fixture.Model.Notice);
    });
}
