using System.Net;
using System.Text.Json.Nodes;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Browser;

/// <summary>
/// The content blocker as part of the browser: <see cref="ContentBlocker"/> (a port of
/// <c>Sources/Mosaic/Model/ContentBlocker.swift</c>) and its wiring into <see cref="BrowserModel"/>
/// and <see cref="Automation"/>. The engine is the stand-in bundle, so these always run.
/// </summary>
public class ContentBlockerTests
{
    private static readonly int ListCount = FilterListCatalog.Defaults.Count;

    /// <summary>A fixture that ships the blocker, with its engine built.</summary>
    private static async Task<BrowserFixture> ShippedAsync(Action<BrowserFixture>? configure = null)
    {
        var fixture = new BrowserFixture(f =>
        {
            f.ShipBlocker();
            configure?.Invoke(f);
        });
        await fixture.Model.Blocker.Building;
        return fixture;
    }

    /// <summary>Puts the shipped lists in place as an earlier run would have, last refreshed <paramref name="age"/> ago.</summary>
    private static void ListsFromEarlierRun(BrowserFixture fixture, TimeSpan age)
    {
        Directory.CreateDirectory(fixture.BlockerLists);
        var written = (fixture.Time.GetUtcNow() - age).UtcDateTime;
        foreach (var file in Directory.GetFiles(Path.Combine(fixture.BlockerResources, "lists")))
        {
            var destination = Path.Combine(fixture.BlockerLists, Path.GetFileName(file));
            File.Copy(file, destination);
            File.SetLastWriteTimeUtc(destination, written);
        }
    }

    private static string ValidList(string title) =>
        $"! Title: {title}\n" + string.Concat(Enumerable.Range(0, 100).Select(index => $"||fresh-{index}.example^\n"));

    // ---- Lifecycle ----

    [Fact]
    public void WithoutTheBundleTheBlockerIsUnavailableAndStaysOutOfTheWay() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => f.Library.Add("aaaa", "Alpha"));
        var model = fixture.Model;
        var blocker = model.Blocker;

        Assert.Equal(BlockerStatus.Unavailable, blocker.Status);
        Assert.False(blocker.IsAvailable);
        Assert.Equal("Unavailable", blocker.StatusLine);
        Assert.False(Directory.Exists(fixture.BlockerLists));

        fixture.OpenGeckoTab("https://example.com/");
        // Gecko is not asked to load what is not there; extensions load as before.
        Assert.Empty(fixture.Gecko.CallsTo("configureBlocker"));
        Assert.Single(fixture.Gecko.CallsTo("installExtension"));

        Assert.False(blocker.IsFiltering("https://example.com/"));
        Assert.Equal(BlockerShield.Off, model.BlockerShield);
        Assert.Equal("Content blocker is not included in this build", model.BlockerHelp);
        Assert.Equal("", model.BlockerBadge);

        await blocker.UpdateListsAsync();
        Assert.Empty(fixture.ListServer.Requests);
    });

    [Fact]
    public void StartInstallsTheShippedListsAndBuildsTheEngine() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var blocker = fixture.Model.Blocker;

        Assert.Equal(BlockerStatus.Ready, blocker.Status);
        Assert.Equal(2 * ListCount, blocker.FilterCount);
        Assert.Equal("0.0-test", blocker.EngineVersion);
        Assert.Equal($"{2 * ListCount} filters · uBO 0.0-test", blocker.StatusLine);
        Assert.Null(blocker.FailureMessage);
        Assert.Equal(ListCount + 1, Directory.GetFiles(fixture.BlockerLists).Length);
        Assert.NotNull(blocker.ListsDate);

        Assert.True(blocker.Filter.ShouldBlock("https://ads.example/x.js", "https://site.example/", "script"));
        Assert.False(blocker.Filter.ShouldBlock("https://cdn.example/x.js", "https://site.example/", "script"));
    });

    [Fact]
    public void ListsAlreadyOnDiskAreKeptAndFreshOnesAreNotRefreshed() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f =>
        {
            ListsFromEarlierRun(f, TimeSpan.FromHours(3));
            // A list refreshed in that earlier run, newer than the shipped snapshot.
            File.WriteAllText(Path.Combine(f.BlockerLists, "easylist.txt"), "! newer\n||a^\n||b^\n||c^\n||d^\n||e^");
        });
        var blocker = fixture.Model.Blocker;

        Assert.Equal(2 * ListCount + 3, blocker.FilterCount);
        Assert.Equal("Lists updated 3 hours ago", blocker.ListsLine);

        await fixture.Time.AdvanceAsync(TimeSpan.FromMinutes(5));
        await blocker.Refreshing;
        Assert.Empty(fixture.ListServer.Requests);
    });

    [Fact]
    public void StaleListsAreRefreshedOnceTheBrowserHasSettled() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f => ListsFromEarlierRun(f, TimeSpan.FromDays(3)));
        var blocker = fixture.Model.Blocker;
        Assert.Equal("Lists updated 3 days ago", blocker.ListsLine);

        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(19));
        Assert.Empty(fixture.ListServer.Requests);

        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(1));
        await blocker.Refreshing;
        // Every download failed (the stub answers 404): nothing is replaced and nothing rebuilt.
        Assert.Equal(ListCount + 1, fixture.ListServer.Requests.Count);
        Assert.Equal("Lists updated 3 days ago", blocker.ListsLine);
        Assert.Equal(2 * ListCount, blocker.FilterCount);
        Assert.False(blocker.IsUpdating);
    });

    [Fact]
    public void UpdatingListsRebuildsBothEngines() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f => ListsFromEarlierRun(f, TimeSpan.FromHours(1)));
        var blocker = fixture.Model.Blocker;
        fixture.OpenGeckoTab("https://example.com/");
        fixture.Gecko.Calls.Clear();
        fixture.ListServer.Respond = request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri == FilterListCatalog.SuffixListUrl
                ? ValidList("x").Replace("! Title: x", "// publicsuffix.org", StringComparison.Ordinal)
                : ValidList(request.RequestUri!.AbsoluteUri)),
        });
        var updating = new List<bool>();
        blocker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContentBlocker.IsUpdating)) updating.Add(blocker.IsUpdating);
        };

        await blocker.UpdateListsAsync();
        await blocker.Building;

        Assert.Equal([true, false], updating);
        Assert.Equal(100 * ListCount, blocker.FilterCount);
        Assert.Equal(BlockerStatus.Ready, blocker.Status);
        Assert.Equal("Lists updated just now", blocker.ListsLine);
        Assert.Single(fixture.Gecko.CallsTo("configureBlocker"));
    });

    [Fact]
    public void AnEngineThatFailsToLoadIsReportedAndGeckoIsStillConfigured() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f => File.WriteAllText(Path.Combine(f.BlockerResources, "lists", "easylist.txt"), "THROW"));
        var blocker = fixture.Model.Blocker;

        Assert.Equal(BlockerStatus.Failed, blocker.Status);
        Assert.Contains("bad list easylist", blocker.FailureMessage);
        Assert.Equal(blocker.FailureMessage, blocker.StatusLine);
        Assert.False(blocker.Filter.ShouldBlock("https://ads.example/x.js", "https://site.example/", "script"));
        // Still "on": Gecko builds an engine of its own, and the switches keep working.
        Assert.True(blocker.IsAvailable);
        fixture.OpenGeckoTab("https://example.com/");
        Assert.Single(fixture.Gecko.CallsTo("configureBlocker"));
    });

    [Fact]
    public void AMissingSuffixListIsAFailure() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f => File.Delete(Path.Combine(f.BlockerResources, "lists", FilterListCatalog.SuffixListFileName)));

        Assert.Equal(BlockerStatus.Failed, fixture.Model.Blocker.Status);
        Assert.Contains("Public Suffix List is missing", fixture.Model.Blocker.FailureMessage);
        Assert.Equal("Filter lists not downloaded yet", fixture.Model.Blocker.ListsLine);
    });

    // ---- Gecko ----

    [Fact]
    public void GeckoGetsTheBlockerBeforeExtensionsEachTimeItStarts() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            new BrowserSettings(f.SettingsPath) { BlockerAllowlist = ["b.example", "a.example"] }.Save();
        });

        fixture.OpenGeckoTab("https://example.com/");

        Assert.Equal(["configureBlocker", "installExtension"],
            fixture.Gecko.Calls.Select(call => call.Method).Where(method => method is "configureBlocker" or "installExtension"));
        var arguments = Assert.Single(fixture.Gecko.CallsTo("configureBlocker"));
        Assert.Equal(Path.Combine(fixture.BlockerResources, "ublock-engine.js"), arguments[0]);
        Assert.Equal(Path.Combine(fixture.BlockerLists, FilterListCatalog.SuffixListFileName), arguments[1]);
        Assert.Equal(
            FilterListCatalog.Defaults.Select(list => Path.Combine(fixture.BlockerLists, list.FileName)),
            Assert.IsType<JsonArray>(arguments[2]).Select(path => (string)path!));
        Assert.Equal(true, arguments[3]);
        Assert.Equal(["a.example", "b.example"], Assert.IsType<JsonArray>(arguments[4]).Select(site => (string)site!));

        // A restarted Gecko is configured again.
        fixture.Gecko.Crash();
        fixture.Model.Reload();
        await TestLoop.Settle();
        Assert.Equal(2, fixture.Gecko.CallsTo("configureBlocker").Count);
    });

    [Fact]
    public void AGeckoThatRefusesTheBlockerStillLoadsExtensions() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f =>
        {
            f.Library.Add("aaaa", "Alpha");
            f.Gecko.Handlers["configureBlocker"] = _ => throw new InvalidOperationException("no such method");
        });

        fixture.OpenGeckoTab("https://example.com/");

        Assert.Single(fixture.Gecko.CallsTo("installExtension"));
    });

    // ---- Switches ----

    [Fact]
    public void TheMasterSwitchIsRememberedAppliedEverywhereAndReloadsThePage() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var model = fixture.Model;
        var (_, browser) = fixture.OpenChromiumTab("https://site.example/");
        await fixture.Gecko.StartAsync();

        model.SetBlockerEnabled(false);

        Assert.False(model.Blocker.IsEnabled);
        Assert.Equal("Content blocker off", model.Notice);
        Assert.Equal(["reload"], browser.Actions);
        var options = Assert.Single(fixture.Gecko.CallsTo("setBlockerOptions"));
        Assert.Equal(false, options[0]);
        Assert.Empty(Assert.IsType<JsonArray>(options[1]));
        Assert.False(model.Blocker.Filter.ShouldBlock("https://ads.example/x.js", "https://site.example/", "script"));
        Assert.Equal(BlockerShield.Off, model.BlockerShield);
        Assert.Equal("Content blocker is off", model.BlockerHelp);
        Assert.False(fixture.ReloadSettings().BlockerEnabled);

        // Setting what is already set does nothing, not even a reload.
        model.SetBlockerEnabled(false);
        Assert.Single(browser.Actions);

        model.ToggleBlocker();
        Assert.True(model.Blocker.IsEnabled);
        Assert.Equal("Content blocker on", model.Notice);
        Assert.True(fixture.ReloadSettings().BlockerEnabled);
        Assert.True(model.Blocker.Filter.ShouldBlock("https://ads.example/x.js", "https://site.example/", "script"));
    });

    [Fact]
    public void TheMasterSwitchIsReadBackOnTheNextRun() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f =>
            new BrowserSettings(f.SettingsPath) { BlockerEnabled = false, BlockerAllowlist = ["www.example.com"] }.Save());

        Assert.False(fixture.Model.Blocker.IsEnabled);
        Assert.Equal(["example.com"], fixture.Model.Blocker.Settings.Allowlist);
        Assert.False(fixture.Model.Blocker.Filter.ShouldBlock("https://ads.example/x.js", "https://other.example/", "script"));
    });

    [Fact]
    public void ThePerSiteSwitchCoversTheSiteAndReloadsOnlyThePageInFront() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var model = fixture.Model;
        var (background, backgroundBrowser) = fixture.OpenChromiumTab("https://www.other.example/");
        var (tab, browser) = fixture.OpenChromiumTab("https://news.site.example/story");
        Assert.True(model.IsBlockingSite);
        Assert.Equal("news.site.example", model.BlockerSite);
        Assert.Equal("On for news.site.example", model.BlockerSiteLabel);

        model.SetBlocking(false, tab);

        Assert.Equal(["news.site.example"], model.Blocker.Settings.Allowlist);
        Assert.Equal(["news.site.example"], fixture.ReloadSettings().BlockerAllowlist);
        Assert.Equal(["reload"], browser.Actions);
        Assert.False(model.IsBlockingSite);
        Assert.False(model.IsFilteringPage);
        Assert.Equal(BlockerShield.Idle, model.BlockerShield);
        Assert.Equal("Content blocker is not active on this page", model.BlockerHelp);
        Assert.False(model.Blocker.Filter.ShouldBlock("https://ads.example/x.js", "https://news.site.example/", "script"));
        Assert.True(model.Blocker.Filter.ShouldBlock("https://ads.example/x.js", "https://other.example/", "script"));

        // A tab that is not in front is not reloaded.
        model.SetBlocking(false, background);
        Assert.Empty(backgroundBrowser.Actions);
        Assert.Equal(["news.site.example", "other.example"], model.Blocker.Settings.Allowlist.Order());

        model.SetBlocking(true, tab);
        Assert.True(model.IsBlockingSite);
        Assert.Equal(["other.example"], fixture.ReloadSettings().BlockerAllowlist);
    });

    [Fact]
    public void PagesThatAreNotOnTheWebHaveNoSiteSwitch() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var model = fixture.Model;

        Assert.Null(model.BlockerSite);
        Assert.False(model.HasBlockerSite);
        Assert.Equal(BlockerShield.Idle, model.BlockerShield);

        var (tab, _) = fixture.OpenChromiumTab("file:///C:/page.html");
        model.SetBlocking(false, tab);
        Assert.Empty(model.Blocker.Settings.Allowlist);
        Assert.False(model.HasBlockerSite);
    });

    // ---- Counts ----

    [Fact]
    public void GeckoTabsShowTheCountGeckoReports() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://site.example/");

        fixture.Gecko.Report(Snapshots.Tab(1, "https://site.example/", selected: true, blocked: 3));
        Assert.Equal(3, tab.BlockedCount);
        Assert.Equal(BlockerShield.Filtering, model.BlockerShield);
        Assert.Equal("3", model.BlockerBadge);
        Assert.Equal("3 requests blocked on this page", model.BlockerHelp);
        Assert.Equal("3 requests blocked on this page", model.BlockedCountText);

        fixture.Gecko.Report(Snapshots.Tab(1, "https://site.example/", selected: true, blocked: 1));
        Assert.Equal("1 request blocked on this page", model.BlockerHelp);

        fixture.Gecko.Report(Snapshots.Tab(1, "https://site.example/", selected: true, blocked: 1500));
        Assert.Equal("999+", model.BlockerBadge);

        // A new page: Gecko started the count over.
        fixture.Gecko.Report(Snapshots.Tab(1, "https://site.example/", selected: true));
        Assert.Equal("", model.BlockerBadge);
        Assert.True(model.IsFilteringPage);
        Assert.Equal("0 requests blocked on this page", model.BlockedCountText);
    });

    [Fact]
    public void ChromiumTabsShowTheCountTheirBrowserReports() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var model = fixture.Model;
        var (tab, browser) = fixture.OpenChromiumTab("https://site.example/");

        browser.RaiseBlockedCount(1);
        browser.RaiseBlockedCount(2);
        Assert.Equal(2, tab.BlockedCount);
        Assert.Equal("2", model.BlockerBadge);

        // No count on a page that is not being filtered, whatever the engine says.
        model.SetBlocking(false, tab);
        Assert.Equal("", model.BlockerBadge);
        model.SetBlocking(true, tab);

        // Moving the tab to the other engine starts the count over.
        model.SetEngine(EngineKind.Gecko);
        Assert.Equal(0, tab.BlockedCount);
        // And the browser the tab let go of no longer counts.
        browser.RaiseBlockedCount(3);
        Assert.Equal(0, tab.BlockedCount);
    });

    [Fact]
    public void OpeningThePopoverRefreshesTheListsAge() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f => ListsFromEarlierRun(f, TimeSpan.FromHours(1)));
        var model = fixture.Model;
        var changed = new List<string?>();
        model.Blocker.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.ShowBlocker = true;
        Assert.Equal([nameof(ContentBlocker.ListsLine)], changed);
        model.ShowBlocker = false;
        Assert.Single(changed);
    });

    // ---- Automation ----

    private static async Task<JsonObject> SendAsync(Automation automation, string command, string? value = null)
    {
        var request = new JsonObject { ["do"] = command };
        if (value is not null) request["value"] = value;
        return JsonNode.Parse((await automation.HandleAsync(request.ToJsonString())).Json)!.AsObject();
    }

    [Fact]
    public void AutomationDrivesAndReportsTheBlocker() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f => ListsFromEarlierRun(f, TimeSpan.FromHours(1)));
        var model = fixture.Model;
        var automation = new Automation(model, _ => Task.CompletedTask);
        fixture.OpenGeckoTab("https://www.site.example/");
        fixture.Gecko.Report(Snapshots.Tab(1, "https://www.site.example/", selected: true, blocked: 7));

        var state = (await SendAsync(automation, "dump"))["state"]!;
        Assert.Equal(7, (int)state["tabs"]![1]!["blocked"]!);
        Assert.Equal(
            $$"""{"status":"ready","enabled":true,"allowlist":[],"engineVersion":"0.0-test","updating":false,"filters":{{2 * ListCount}},"error":""}""",
            state["blocker"]!.ToJsonString());

        Assert.True((bool)(await SendAsync(automation, "blockerSite", "off"))["ok"]!);
        Assert.Equal(["site.example"], model.Blocker.Settings.Allowlist);
        Assert.True((bool)(await SendAsync(automation, "blockerSite", "on"))["ok"]!);
        Assert.Empty(model.Blocker.Settings.Allowlist);

        Assert.True((bool)(await SendAsync(automation, "blocker", "off"))["ok"]!);
        Assert.False(model.Blocker.IsEnabled);
        Assert.False((bool)(await SendAsync(automation, "dump"))["state"]!["blocker"]!["enabled"]!);
        Assert.True((bool)(await SendAsync(automation, "blocker", "on"))["ok"]!);
        Assert.True(model.Blocker.IsEnabled);

        Assert.True((bool)(await SendAsync(automation, "showBlocker"))["ok"]!);
        Assert.True(model.ShowBlocker);
        Assert.True((bool)(await SendAsync(automation, "showBlocker", "false"))["ok"]!);
        Assert.False(model.ShowBlocker);

        Assert.True((bool)(await SendAsync(automation, "updateLists"))["ok"]!);
        await Eventually.TrueAsync(() => fixture.ListServer.Requests.Count == ListCount + 1 && !model.Blocker.IsUpdating);

        // The per-site switch needs a web page in front.
        model.NewTab();
        var refused = await SendAsync(automation, "blockerSite", "off");
        Assert.False((bool)refused["ok"]!);
        Assert.Contains("not a web page", (string)refused["error"]!);
    });

    // ---- Formatting ----

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(-30, "just now")] // a clock that went back
    [InlineData(59, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(150, "2 minutes ago")]
    [InlineData(3_600, "1 hour ago")]
    [InlineData(5 * 3_600, "5 hours ago")]
    [InlineData(86_400, "1 day ago")]
    [InlineData(3 * 86_400, "3 days ago")]
    [InlineData(7 * 86_400, "1 week ago")]
    [InlineData(20 * 86_400, "2 weeks ago")]
    [InlineData(45 * 86_400, "1 month ago")]
    [InlineData(400 * 86_400, "1 year ago")]
    public void ListAgesReadNaturally(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, ContentBlocker.RelativeTime(now.AddSeconds(-secondsAgo), now));
    }
}
