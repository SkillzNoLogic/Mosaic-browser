using System.Net;
using System.Text.Json.Nodes;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Blocker;

/// <summary>
/// The privacy area's part of <see cref="ContentBlocker"/> (<c>ContentBlocker.Privacy.cs</c>):
/// optional lists, My filters, cosmetic switch and the dashboard, on the stand-in engine.
/// </summary>
public sealed class ContentBlockerPrivacyTests
{
    private static string ValidList(string title, string host) =>
        $"! Title: {title}\n" + string.Concat(Enumerable.Range(0, 100).Select(index => $"||{host}-{index}.example^\n"));

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

    [Fact]
    public void UserFiltersAreCompiledIntoBothEngines() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var blocker = fixture.Model.Blocker;
        var before = blocker.FilterCount;
        await fixture.Gecko.StartAsync();

        blocker.SaveUserFilters("||mine.example^\n||yours.example^\nexample.com##.ad");
        await blocker.CurrentBuild;

        Assert.Equal(before + 2, blocker.FilterCount);
        Assert.Equal("||mine.example^\n||yours.example^\nexample.com##.ad", blocker.UserFilters);
        var paths = (JsonArray)fixture.Gecko.CallsTo("configureBlocker")[^1][2]!;
        Assert.EndsWith("user-filters.txt", paths[^1]!.GetValue<string>());
    });

    [Fact]
    public void TurningOnAnOptionalListDownloadsItThenRebuilds() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f => f.ListServer.Respond = request =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidList("Cookies", "cookie")) }));
        var blocker = fixture.Model.Blocker;
        var before = blocker.FilterCount;
        var cookies = FilterListCatalog.Optional.First(list => list.Id == "fanboy-cookiemonster");

        blocker.SetList(cookies, enabled: true);
        Assert.True(blocker.IsUpdating);
        while (blocker.IsUpdating) await Task.Delay(10);
        await blocker.CurrentBuild;

        Assert.Equal(cookies.Url, Assert.Single(fixture.ListServer.Requests));
        Assert.True(blocker.IsListDownloaded(cookies));
        Assert.Equal(before + 100, blocker.FilterCount);
        Assert.Equal("All lists are up to date.", blocker.LastUpdateMessage);
        Assert.Contains("fanboy-cookiemonster", File.ReadAllText(fixture.PathFor("options.json")));

        blocker.SetList(cookies, enabled: false);
        await blocker.CurrentBuild;
        Assert.Equal(before, blocker.FilterCount);
    });

    [Fact]
    public void ChosenListsAreUsedFromTheFirstBuild() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync(f =>
            new BlockerOptions().WithList("easylist", false).Save(f.PathFor("options.json")));
        var all = FilterListCatalog.Defaults.Count * 2;
        Assert.Equal(all - 2, fixture.Model.Blocker.FilterCount);
        Assert.DoesNotContain("easylist", fixture.Model.Blocker.EnabledListIds);
    });

    [Fact]
    public void CosmeticSwitchIsSavedAndSentToGecko() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var blocker = fixture.Model.Blocker;
        await fixture.Gecko.StartAsync();
        Assert.True(blocker.CosmeticEnabled);
        blocker.SetCosmeticEnabled(false);
        Assert.False(blocker.Filter.CosmeticsEnabled);
        Assert.Equal(false, Assert.Single(fixture.Gecko.CallsTo("setCosmeticOptions"))[0]);
        Assert.False(BlockerOptions.Load(fixture.PathFor("options.json")).CosmeticEnabled);
    });

    [Fact]
    public void SettingsCanAllowlistSitesDirectly() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var blocker = fixture.Model.Blocker;
        blocker.SetAllowlisted(true, "https://www.news.example/page");
        Assert.Contains("news.example", blocker.Settings.Allowlist);
        Assert.Contains("news.example", fixture.ReloadSettings().BlockerAllowlist);
        blocker.SetAllowlisted(false, "news.example");
        Assert.Empty(blocker.Settings.Allowlist);
    });

    [Fact]
    public void DashboardListsGeckoBlocksWithTheirFilters() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        fixture.Gecko.Handlers["blockedRequests"] = _ => JsonNode.Parse("""[{"url":"https://ads.example/a.js","type":"script","count":3,"filter":"||ublock-filters-0.example^"}]""");
        var tab = fixture.OpenGeckoTab("https://site.example/");
        var requests = await fixture.Model.Blocker.BlockedRequestsAsync(tab);
        var request = Assert.Single(requests);
        Assert.Equal(3, request.Count);
        Assert.Equal("ads.example", request.Host);
        Assert.Equal("uBlock filters – Ads", request.List);
    });

    [Fact]
    public void DashboardListsChromiumBlocksFromTheLog() => TestLoop.Run(async () =>
    {
        using var fixture = await ShippedAsync();
        var (tab, _) = fixture.OpenChromiumTab("https://site.example/");
        Assert.True(fixture.Model.Blocker.Filter.ShouldBlock("https://ads.example/x.js", "https://site.example/", "script"));
        var request = Assert.Single(await fixture.Model.Blocker.BlockedRequestsAsync(tab));
        Assert.Equal("https://ads.example/x.js", request.Url);
        Assert.Equal("", request.Filter); // the stand-in engine cannot name filters
    });
}
