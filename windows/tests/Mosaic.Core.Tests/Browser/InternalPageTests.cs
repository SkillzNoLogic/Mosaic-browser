using Mosaic.Core.Browser;

namespace Mosaic.Core.Tests.Browser;

/// <summary><c>mosaic://&lt;host&gt;</c> addresses: Mosaic's own pages, shown without an engine.</summary>
public class InternalPageTests
{
    [Theory]
    [InlineData("mosaic://bookmarks", "bookmarks")]
    [InlineData("MOSAIC://Reading-List/", "reading-list")]
    [InlineData("mosaic://history?q=x", "history")]
    [InlineData("mosaic://downloads#top", "downloads")]
    [InlineData("mosaic://", null)]
    [InlineData("https://bookmarks", null)]
    [InlineData("mosaic:bookmarks", null)]
    [InlineData(null, null)]
    public void HostIsReadFromTheAddress(string? url, string? host) => Assert.Equal(host, BrowserModel.InternalPageHost(url));

    [Fact]
    public void TypedInternalAddressIsShownWithoutAnEngine() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;

        model.Navigate("  mosaic://reading-list ");

        var tab = model.Tabs[0];
        Assert.Equal("mosaic://reading-list", tab.Url);
        Assert.Equal("Reading List", tab.Title);
        Assert.False(tab.IsLoading);
        Assert.Equal(ContentKind.InternalPage, model.Content);
        Assert.Equal("reading-list", model.InternalPage);
        Assert.False(model.ShowsGeckoPage);
        Assert.Null(model.Message);
        // Nothing was asked of either engine.
        Assert.Equal(0, fixture.Gecko.StartCount);
        Assert.Empty(fixture.Chromium.Created);
    });

    [Fact]
    public void AGeckoPageGivesWayToAnInternalPage() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var tab = fixture.OpenGeckoTab("https://example.com/");
        var geckoId = tab.GeckoId!.Value;

        fixture.Model.Load("mosaic://bookmarks", tab);

        Assert.Null(tab.GeckoId);
        Assert.Contains([geckoId], fixture.Gecko.CallsTo("closeTab"));
        Assert.Equal("bookmarks", fixture.Model.InternalPage);
        Assert.False(fixture.Model.CanGoBack);
    });

    [Fact]
    public void AChromiumPageGivesWayToAnInternalPageAndBack() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var (tab, browser) = fixture.OpenChromiumTab("https://example.org/");

        fixture.Model.Load("mosaic://history", tab);

        Assert.True(browser.IsClosed);
        Assert.Null(tab.Chromium);
        Assert.Equal(ContentKind.InternalPage, fixture.Model.Content);

        fixture.Model.Load("https://example.org/", tab);

        Assert.Null(fixture.Model.InternalPage);
        Assert.NotNull(tab.Chromium);
        Assert.Equal(EngineKind.Chromium, tab.Engine);
    });

    [Fact]
    public void SelectingAnotherTabLeavesTheInternalPage() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var internalTab = model.Tabs[0];
        model.Navigate("mosaic://downloads");
        var other = model.NewTab();

        Assert.Null(model.InternalPage);
        Assert.Equal(ContentKind.StartPage, model.Content);

        model.Select(internalTab);

        Assert.Equal("downloads", model.InternalPage);
        _ = other;
    });
}
