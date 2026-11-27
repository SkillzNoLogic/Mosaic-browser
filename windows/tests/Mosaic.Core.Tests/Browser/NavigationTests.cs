using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Browser;

public class NavigationTests
{
    [Fact]
    public void AddressIsOpenedInGeckoByDefault() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var tab = model.Tabs[0];

        model.Navigate("example.com");

        Assert.Equal(1, fixture.Gecko.StartCount);
        Assert.Equal(["https://example.com", true], Assert.Single(fixture.Gecko.CallsTo("openTab")));
        Assert.Equal(1, tab.GeckoId);
        Assert.Equal("https://example.com", tab.Url);
        Assert.True(tab.IsLoading);
        Assert.Equal("https://example.com", model.AddressText);
        Assert.Equal(ContentKind.GeckoPage, model.Content);
        Assert.True(model.ShowsGeckoPage);
        Assert.True(model.IsLoading);
        Assert.True(model.CanReload);
        Assert.Empty(fixture.Chromium.Created);
    });

    [Fact]
    public void AnythingElseIsSearchedWithTheChosenSearchEngine() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        fixture.Model.SearchEngine = SearchEngine.DuckDuckGo;

        fixture.Model.Navigate("two engines");

        Assert.Equal("https://search.test/duckduckgo?q=two%20engines", fixture.Model.Tabs[0].Url);
    });

    [Fact]
    public void EmptyInputDoesNothing() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();

        fixture.Model.Navigate("   ");

        Assert.True(fixture.Model.Tabs[0].IsStartPage);
        Assert.Equal(0, fixture.Gecko.StartCount);
    });

    [Fact]
    public void TabThatGeckoAlreadyHasIsNavigatedNotReopened() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var tab = fixture.OpenGeckoTab("https://example.com/");

        fixture.Model.Navigate("https://example.org/");

        Assert.Single(fixture.Gecko.CallsTo("openTab"));
        Assert.Equal([tab.GeckoId, "https://example.org/"], Assert.Single(fixture.Gecko.CallsTo("navigate")));
        Assert.Equal("", tab.Title);
    });

    [Fact]
    public void BackgroundTabIsOpenedUnselectedInGecko() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();

        fixture.Model.NewTab("https://example.com/", select: false);

        Assert.Equal(["https://example.com/", false], Assert.Single(fixture.Gecko.CallsTo("openTab")));
    });

    [Fact]
    public void ChromiumTabGetsItsOwnBrowserAndReusesIt() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");

        Assert.Equal(["https://example.com/"], browser.Loads);
        Assert.Equal(ContentKind.ChromiumPage, model.Content);
        Assert.True(browser.IsShown);
        Assert.False(model.ShowsGeckoPage);
        Assert.Equal(0, fixture.Gecko.StartCount);

        model.Navigate("https://example.org/");

        Assert.Same(browser, tab.Chromium);
        Assert.Single(fixture.Chromium.Created);
        Assert.Equal(["https://example.com/", "https://example.org/"], browser.Loads);
    });

    [Fact]
    public void ChromiumTabWithoutTheEngineExplainsItself() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
        {
            f.Chromium.IsAvailable = false;
            f.Chromium.UnavailableReason = "Install the WebView2 Runtime.";
        });
        var model = fixture.Model;

        var tab = model.NewTab("https://example.com/", EngineKind.Chromium);

        Assert.Equal("Install the WebView2 Runtime.", tab.Failure);
        Assert.False(tab.IsLoading);
        Assert.Null(tab.Chromium);
        Assert.Equal(ContentKind.Failure, model.Content);
        Assert.Equal(new ContentMessage("This page can’t be shown", "Install the WebView2 Runtime.", false, true), model.Message);
        Assert.False(model.ChromiumAvailable);
    });

    [Fact]
    public void HistoryAndReloadGoToTheSelectedTabsEngine() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var geckoTab = fixture.OpenGeckoTab("https://example.com/");
        var (chromiumTab, browser) = fixture.OpenChromiumTab("https://example.org/");
        fixture.Gecko.Calls.Clear();

        model.GoBack();
        model.GoForward();
        model.Reload();
        model.StopLoading();
        Assert.Equal(["back", "forward", "reload", "stop"], browser.Actions);
        Assert.Empty(fixture.Gecko.Calls);

        model.Select(geckoTab);
        fixture.Gecko.Calls.Clear();
        model.GoBack();
        model.GoForward();
        model.Reload();
        model.StopLoading();
        Assert.Equal(
            ["goBack(1)", "goForward(1)", "reload(1)", "stop(1)"],
            fixture.Gecko.Calls.Select(call => call.ToString()));
        Assert.Equal(4, browser.Actions.Count);
        Assert.Same(geckoTab, model.SelectedTab);
        Assert.NotSame(chromiumTab, model.SelectedTab);
    });

    [Fact]
    public void ReloadOnTheStartPageDoesNothing() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();

        fixture.Model.Reload();
        fixture.Model.GoBack();
        fixture.Model.StopLoading();

        Assert.False(fixture.Model.CanReload);
        Assert.Equal(0, fixture.Gecko.StartCount);
    });

    [Fact]
    public void ToolbarButtonStopsWhileLoadingAndReloadsOtherwise() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var (_, browser) = fixture.OpenChromiumTab("https://example.com/");
        Assert.True(fixture.Model.IsLoading);

        fixture.Model.ReloadOrStop();
        Assert.Equal(["stop"], browser.Actions);

        browser.RaiseLoadState(loading: false);
        Assert.False(fixture.Model.IsLoading);
        fixture.Model.ReloadOrStop();
        Assert.Equal(["stop", "reload"], browser.Actions);
    });

    [Fact]
    public void GeckoThatCannotStartIsReportedOnTheTabAndReloadRetries() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f =>
            f.Gecko.StartFailure = new InvalidOperationException("No Gecko runtime found."));
        var model = fixture.Model;

        model.Navigate("example.com");

        var tab = model.Tabs[0];
        Assert.Equal("No Gecko runtime found.", tab.Failure);
        Assert.False(tab.IsLoading);
        Assert.Equal(ContentKind.Failure, model.Content);
        Assert.True(model.ShowsMessage);
        Assert.True(model.Message!.IsFailure);
        Assert.False(model.ShowsGeckoPage);

        fixture.Gecko.StartFailure = null;
        model.Reload();

        Assert.Null(tab.Failure);
        Assert.Equal(1, tab.GeckoId);
        Assert.True(model.ShowsGeckoPage);
        Assert.False(model.ShowsMessage);
    });

    [Fact]
    public void WhileGeckoStartsTheTabSaysSoAndOnlyTheLastAddressIsOpened() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture(f => f.Gecko.StartGate = new TaskCompletionSource());
        var model = fixture.Model;

        model.Navigate("https://one.example/");
        Assert.Equal(ContentKind.GeckoStarting, model.Content);
        Assert.Equal(new ContentMessage("Starting Mozilla engine…", "Firefox 140.5.0", true, false), model.Message);
        Assert.False(model.ShowsGeckoPage);

        model.Navigate("https://two.example/");
        fixture.Gecko.StartGate!.SetResult();

        // One launch, one Gecko tab, on the address the user ended up asking for.
        Assert.Equal(1, fixture.Gecko.StartCount);
        Assert.Equal(["https://two.example/", true], Assert.Single(fixture.Gecko.CallsTo("openTab")));
        Assert.Empty(fixture.Gecko.CallsTo("navigate"));
        Assert.Equal(1, model.Tabs[0].GeckoId);
        Assert.True(model.ShowsGeckoPage);
    });

    [Fact]
    public void SecondLoadWaitsForTheOpenInFlightInsteadOfOpeningTwice() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        fixture.Gecko.Hold("openTab");

        model.Navigate("https://one.example/");
        Assert.Equal(ContentKind.GeckoOpening, model.Content);
        Assert.Equal("Opening page…", model.Message!.Title);
        model.Navigate("https://two.example/");
        Assert.Single(fixture.Gecko.CallsTo("openTab"));

        fixture.Gecko.Release("openTab");

        var tab = model.Tabs[0];
        Assert.Equal(1, tab.GeckoId);
        Assert.Single(fixture.Gecko.CallsTo("openTab"));
        Assert.Equal([1, "https://two.example/"], Assert.Single(fixture.Gecko.CallsTo("navigate")));
        Assert.Equal("https://two.example/", tab.Url);
    });

    [Fact]
    public void TabClosedWhileGeckoOpensItLeavesNoOrphanBehind() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        fixture.Gecko.Hold("openTab");
        var doomed = model.NewTab("https://example.com/");

        model.Close(doomed);
        fixture.Gecko.Release("openTab");

        Assert.Equal([1], Assert.Single(fixture.Gecko.CallsTo("closeTab")));
        Assert.Null(doomed.GeckoId);

        // A snapshot taken before the close landed still lists the tab; it must stay closed.
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com/", selected: true));
        Assert.Single(model.Tabs);
        Assert.DoesNotContain(model.Tabs, tab => tab.GeckoId == 1);
    });

    [Fact]
    public void TabMovedAwayAndBackWhileGeckoOpensItEndsUpWithExactlyOneGeckoTab() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        fixture.Gecko.Hold("openTab");
        var tab = model.NewTab("https://example.com/");

        model.SetEngine(EngineKind.Chromium);
        model.SetEngine(EngineKind.Gecko);
        Assert.Equal(2, fixture.Gecko.CallsTo("openTab").Count);

        // The first open belongs to the tab's earlier life in Gecko: it is closed, not claimed.
        fixture.Gecko.Release("openTab");
        Assert.Null(tab.GeckoId);
        Assert.Equal([1], Assert.Single(fixture.Gecko.CallsTo("closeTab")));

        fixture.Gecko.Release("openTab");
        Assert.Equal(2, tab.GeckoId);
        Assert.Single(fixture.Gecko.CallsTo("closeTab"));

        fixture.Gecko.Report(
            Snapshots.Tab(1, "https://example.com/"), Snapshots.Tab(2, "https://example.com/", selected: true));
        Assert.Equal(2, model.Tabs.Count);
        Assert.Single(model.Tabs, candidate => candidate.GeckoId is not null);
    });

    [Fact]
    public void OpenThatFailsInGeckoIsReportedOnTheTab() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        fixture.Gecko.Hold("openTab");
        var tab = fixture.Model.NewTab("https://example.com/");

        fixture.Gecko.Fail("openTab", new InvalidOperationException("Gecko has no browser window"));

        Assert.Equal("Gecko has no browser window", tab.Failure);
        Assert.False(tab.IsLoading);
        Assert.Equal(ContentKind.Failure, fixture.Model.Content);

        // Unknown tabs are adopted again afterwards: the failed open is not left "pending".
        fixture.Gecko.Report(Snapshots.Tab(9, "https://popup.example/"));
        Assert.Contains(fixture.Model.Tabs, candidate => candidate.GeckoId == 9);
    });

    [Fact]
    public void OpenThatReturnsNoTabIsAFailureNotAnEndlessWait() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        fixture.Gecko.Handlers["openTab"] = _ => JsonValue.Create("not a tab id");

        var tab = fixture.Model.NewTab("https://example.com/");

        Assert.Null(tab.GeckoId);
        Assert.Equal("Gecko did not open the tab.", tab.Failure);
    });

    [Fact]
    public void ReloadAfterGeckoStoppedStartsItAgain() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://example.com/");

        fixture.Gecko.Crash();
        Assert.Equal(GeckoStatus.Stopped, fixture.Gecko.Status);
        model.Reload();

        Assert.Equal(2, fixture.Gecko.StartCount);
        Assert.Equal(2, fixture.Gecko.CallsTo("openTab").Count);
        Assert.Null(tab.Failure);
        Assert.Equal(2, tab.GeckoId);
        Assert.Empty(fixture.Gecko.CallsTo("reload"));
    });

    [Fact]
    public void NavigatingHandsFocusToAChromiumPage() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var (_, browser) = fixture.OpenChromiumTab("https://example.com/");
        Assert.DoesNotContain("focus", browser.Actions);

        fixture.Model.Navigate("https://example.org/");

        Assert.Equal(["focus"], browser.Actions);
    });

    [Fact]
    public void NavigatingHandsFocusToAGeckoPageAfterABeat() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();

        fixture.Model.Navigate("example.com");
        Assert.Empty(fixture.Gecko.CallsTo("focusContent"));
        Assert.Equal(0, fixture.GeckoFocusRequests);

        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));

        Assert.Single(fixture.Gecko.CallsTo("focusContent"));
        Assert.Equal(1, fixture.GeckoFocusRequests);
    });

    [Fact]
    public void GeckoPageDoesNotTakeFocusFromTheAddressFieldOrAnotherTab() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;

        model.Navigate("example.com");
        model.BeginAddressEditing();
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(0, fixture.GeckoFocusRequests);

        model.EndAddressEditing();
        model.Navigate("example.org");
        model.NewTab();
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(0, fixture.GeckoFocusRequests);
        Assert.Empty(fixture.Gecko.CallsTo("focusContent"));
    });

    [Fact]
    public void NothingTakesFocusWhenFocusChangesAreOff() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        model.AllowsFocusChanges = false;

        model.Navigate("example.com");
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        var (_, browser) = fixture.OpenChromiumTab("https://example.org/");
        model.Navigate("https://example.net/");

        Assert.Equal(0, fixture.GeckoFocusRequests);
        Assert.Empty(fixture.Gecko.CallsTo("focusContent"));
        Assert.DoesNotContain("focus", browser.Actions);
    });

    [Fact]
    public void PageDrivenAddressChangesDoNotOverwriteWhatIsBeingTyped() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");

        model.BeginAddressEditing();
        model.AddressText = "half-typed";
        browser.RaiseUrl("https://example.com/redirected");

        Assert.Equal("https://example.com/redirected", tab.Url);
        Assert.Equal("half-typed", model.AddressText);

        // Leaving the field without submitting drops the draft.
        model.EndAddressEditing();
        Assert.False(model.IsEditingAddress);
        Assert.Equal("https://example.com/redirected", model.AddressText);

        browser.RaiseUrl("https://example.com/next");
        Assert.Equal("https://example.com/next", model.AddressText);
    });

    [Fact]
    public void SubmittingAnAddressEndsEditing() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        fixture.Model.BeginAddressEditing();
        fixture.Model.AddressText = "example.com";

        fixture.Model.Navigate(fixture.Model.AddressText);

        Assert.False(fixture.Model.IsEditingAddress);
        Assert.Equal("https://example.com", fixture.Model.AddressText);
    });

    [Theory]
    [InlineData("", AddressKind.Search)]
    [InlineData("two engines", AddressKind.Search)]
    [InlineData("https://example.com/", AddressKind.Secure)]
    [InlineData("http://example.com/", AddressKind.Web)]
    [InlineData("example.com", AddressKind.Web)]
    public void AddressIconFollowsWhatIsTyped(string text, AddressKind expected) => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();

        fixture.Model.AddressText = text;

        Assert.Equal(expected, fixture.Model.AddressKind);
    });

    [Fact]
    public void ChromiumEventsAreMirroredOntoTheTabAndToolbar() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");

        browser.RaiseUrl("https://www.example.com/");
        browser.RaiseTitle("Example Domain");
        browser.RaiseLoadState(loading: false, canGoBack: true, canGoForward: true);

        Assert.Equal("https://www.example.com/", tab.Url);
        Assert.Equal("https://www.example.com/", model.AddressText);
        Assert.Equal("Example Domain", tab.Title);
        Assert.False(model.IsLoading);
        Assert.True(model.CanGoBack);
        Assert.True(model.CanGoForward);
    });

    [Fact]
    public void BackgroundTabDoesNotDriveTheToolbar() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (_, browser) = fixture.OpenChromiumTab("https://example.com/");
        model.NewTab();

        browser.RaiseUrl("https://example.com/elsewhere");
        browser.RaiseLoadState(loading: false, canGoBack: true);

        Assert.Equal("", model.AddressText);
        Assert.False(model.CanGoBack);
        Assert.False(model.IsLoading);
    });

    [Fact]
    public void BlankPageChromiumStartsFromDoesNotFlashIntoTheAddressBar() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");

        browser.RaiseUrl("about:blank");
        Assert.Equal("https://example.com/", tab.Url);
        Assert.Equal("https://example.com/", fixture.Model.AddressText);

        // Once the load has settled, a page that really is blank is reported as such.
        browser.RaiseLoadState(loading: false);
        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(2));
        browser.RaiseUrl("about:blank");
        Assert.Equal("about:blank", tab.Url);
    });

    [Fact]
    public void NewWindowRequestFromChromiumOpensAChromiumTabNextToItsSource() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (source, browser) = fixture.OpenChromiumTab("https://example.com/");
        var trailing = model.NewTab(select: false);

        browser.RaiseNewTab("https://popup.example/");

        Assert.Equal(4, model.Tabs.Count);
        var popup = model.Tabs[model.Tabs.IndexOf(source) + 1];
        Assert.NotSame(trailing, popup);
        Assert.Equal(EngineKind.Chromium, popup.Engine);
        Assert.Equal("https://popup.example/", popup.Url);
        Assert.Same(popup, model.SelectedTab);
        Assert.Equal(2, fixture.Chromium.Created.Count);
        // Only the selected tab's page is showing.
        Assert.False(browser.IsShown);
        Assert.True(fixture.Chromium.Created[1].IsShown);
    });

    [Fact]
    public void ChromiumFailureShowsTheReasonAndReloadStartsAFreshBrowser() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");

        browser.RaiseFailed("Chromium stopped unexpectedly.");

        Assert.True(browser.IsClosed);
        Assert.Null(tab.Chromium);
        Assert.Equal("Chromium stopped unexpectedly.", tab.Failure);
        Assert.False(tab.IsLoading);
        Assert.Equal(ContentKind.Failure, model.Content);

        model.Reload();

        Assert.Equal(2, fixture.Chromium.Created.Count);
        Assert.Same(fixture.Chromium.Created[1], tab.Chromium);
        Assert.Equal(["https://example.com/"], fixture.Chromium.Created[1].Loads);
        Assert.Null(tab.Failure);
        Assert.Equal(ContentKind.ChromiumPage, model.Content);
    });

    [Fact]
    public void ChromiumPageIsHiddenWheneverOneOfMosaicsOwnViewsHasTheContentArea() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://example.com/");
        Assert.True(browser.IsShown);

        var blank = model.NewTab();
        Assert.False(browser.IsShown);
        Assert.True(model.ShowsStartPage);

        model.Select(tab);
        Assert.True(browser.IsShown);
        Assert.False(model.ShowsStartPage);

        var geckoTab = fixture.OpenGeckoTab("https://example.org/");
        Assert.False(browser.IsShown);
        Assert.True(model.ShowsGeckoPage);

        model.Close(geckoTab);
        model.Close(blank);
        Assert.Same(tab, model.SelectedTab);
        Assert.True(browser.IsShown);
        Assert.False(model.ShowsGeckoPage);
    });

    [Fact]
    public void ShutdownClosesChromiumBrowsersAndStopsGeckoOnce() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var geckoTab = fixture.OpenGeckoTab("https://example.org/");
        var (_, browser) = fixture.OpenChromiumTab("https://example.com/");

        model.Shutdown();
        model.Shutdown();

        Assert.True(browser.IsClosed);
        Assert.Equal(1, fixture.Gecko.StopAndWaitCount);
        // Gecko going away during shutdown is not a failure to show on the way out.
        Assert.Null(geckoTab.Failure);
    });
}
