using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Mosaic.Core.Tests.Browser;
using static Mosaic.Core.Tests.Browser.Snapshots;
using static Mosaic.Core.Tests.Productivity.ProductivityTestSupport;

namespace Mosaic.Core.Tests.Productivity;

public class ReaderModeTests
{
    private const string Article = "https://news.example/story";

    [Fact]
    public void GeckoArticleBecomesAvailableAndTogglesThroughTheAgent() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        var reader = model.Productivity.Reader;
        fixture.Gecko.Handlers["readerInfo"] = _ => new JsonObject { ["available"] = true, ["active"] = false };
        await model.Productivity.StartAsync();
        var tab = fixture.OpenGeckoTab(Article);
        var id = tab.GeckoId!.Value;
        fixture.Gecko.Report(Tab(id, Article, "Story", selected: true));
        Assert.False(reader.IsAvailable(tab));

        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(0.3));
        Assert.True(reader.IsAvailable(tab));

        reader.Update(reader.Settings with { FontSize = 7 });
        model.ToggleReaderMode();
        await TestLoop.Settle();
        var prefs = (JsonObject)fixture.Gecko.CallsTo("setReaderPrefs").Last()[0]!;
        Assert.Equal(7, prefs["reader.font_size"]!.GetValue<int>());
        Assert.Equal([id, true], fixture.Gecko.CallsTo("setReaderMode").Single());

        var readerUrl = "about:reader?url=" + Uri.EscapeDataString(Article);
        fixture.Gecko.Report(Tab(id, readerUrl, "Story", selected: true));
        Assert.True(reader.IsActive(tab));
        Assert.True(reader.IsAvailable(tab));

        // The user changed the theme in Gecko's own toolbar; leaving takes it over.
        fixture.Gecko.Handlers["readerPrefs"] = _ => new JsonObject { ["reader.color_scheme"] = "sepia", ["reader.font_size"] = 7 };
        model.ToggleReaderMode();
        await TestLoop.Settle();
        Assert.Equal([id, false], fixture.Gecko.CallsTo("setReaderMode").Last());
        Assert.Equal(ReaderTheme.Sepia, reader.Settings.Theme);
        fixture.Gecko.Report(Tab(id, Article, "Story", selected: true));
        Assert.False(reader.IsActive(tab));
    });

    [Fact]
    public void SettingsChangedInMosaicReloadOpenGeckoReaderPages() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var tab = fixture.OpenGeckoTab(Article);
        var id = tab.GeckoId!.Value;
        fixture.Gecko.Report(Tab(id, "about:reader?url=" + Uri.EscapeDataString(Article), selected: true));
        Assert.True(model.Productivity.Reader.IsActive(tab));

        model.Productivity.Reader.Update(new ReaderSettings { Theme = ReaderTheme.Dark });
        await TestLoop.Settle();
        Assert.Equal("dark", ((JsonObject)fixture.Gecko.CallsTo("setReaderPrefs").Last()[0]!)["reader.color_scheme"]!.GetValue<string>());
        Assert.Equal([id], fixture.Gecko.CallsTo("reload").Last());
    });

    [Fact]
    public void ChromiumProbesWithReadabilityAndDrawsTheReaderOverThePage() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        var reader = model.Productivity.Reader;
        await model.Productivity.StartAsync();
        var (tab, browser) = fixture.OpenChromiumTab(Article);
        var host = FakePageScriptHost.For(browser);
        browser.RaiseLoadState(loading: false);
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        await Until(() => reader.IsAvailable(tab));
        Assert.Contains(host.Scripts, s => s.Contains("return isProbablyReaderable(document);"));
        Assert.True(reader.IsAvailable(tab));

        reader.Toggle(tab);
        await Pump();
        var enter = host.Scripts.Last();
        Assert.Contains("chrome.webview.postMessage(\"__mosaicReader:\" + message)", enter);
        Assert.Contains("function Readability(", enter);
        Assert.EndsWith("window.__mosaicReader.enter({\"font\":\"sans-serif\",\"fontSize\":5,\"contentWidth\":3,\"lineHeight\":4,\"theme\":\"light\"});", enter);
        Assert.True(reader.IsActive(tab));
        Assert.Equal(1, host.ListenerCount);

        // The reader page's own toolbar changed a setting.
        host.Post("""__mosaicReader:{"type":"settings","settings":{"font":"serif","fontSize":6,"contentWidth":3,"lineHeight":4,"theme":"dark"}}""");
        Assert.Equal(ReaderFont.Serif, reader.Settings.Font);
        Assert.Equal(ReaderTheme.Dark, reader.Settings.Theme);
        await model.Productivity.Files.Flush();
        Assert.Contains("\"theme\":\"dark\"", File.ReadAllText(model.Productivity.Files.PathOf(ProductivityFiles.ReaderFile)));

        // Messages of other areas are ignored.
        host.Post("""{"type":"closed"}""");
        Assert.True(reader.IsActive(tab));
        host.Post("""__mosaicReader:{"type":"closed"}""");
        Assert.False(reader.IsActive(tab));
        Assert.Equal(0, host.ListenerCount);
    });

    [Fact]
    public void ChromiumReaderEndsWhenANewPageLoadsAndLeavingRunsExit() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        var reader = model.Productivity.Reader;
        await model.Productivity.StartAsync();
        var (tab, browser) = fixture.OpenChromiumTab(Article);
        var host = FakePageScriptHost.For(browser);
        browser.RaiseLoadState(loading: false);
        reader.SetActive(true, tab);
        await Pump();
        Assert.True(reader.IsActive(tab));

        reader.SetActive(false, tab);
        Assert.Contains("window.__mosaicReader.exit()", host.Scripts.Last());
        Assert.False(reader.IsActive(tab));

        reader.SetActive(true, tab);
        await Pump();
        Assert.True(reader.IsActive(tab));
        browser.RaiseLoadState(loading: true);
        browser.RaiseUrl("https://news.example/other");
        Assert.False(reader.IsActive(tab));
        Assert.Equal(0, host.ListenerCount);
    });

    [Fact]
    public void PagesThatAreNotArticlesSaySo() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var (tab, browser) = fixture.OpenChromiumTab(Article);
        FakePageScriptHost.For(browser).Respond = script => script.Contains("__mosaicReader.enter") ? """{"ok":false,"reason":"no-article"}""" : "false";
        browser.RaiseLoadState(loading: false);
        model.Productivity.Reader.SetActive(true, tab);
        await Pump();
        Assert.False(model.Productivity.Reader.IsActive(tab));
        Assert.Equal("Reader view isn’t available for this page.", model.Notice);

        model.Select(model.Tabs[0]);
        model.ToggleReaderMode();
        Assert.Equal("Reader view is for web pages.", model.Notice);
    });

    [Fact]
    public void MissingScriptsAreReported() => TestLoop.Run(async () =>
    {
        using var fixture = Create(readerResources: Path.Combine(Path.GetTempPath(), "no-reader-" + Guid.NewGuid().ToString("N")));
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var (tab, browser) = fixture.OpenChromiumTab(Article);
        browser.RaiseLoadState(loading: false);
        model.Productivity.Reader.SetActive(true, tab);
        await Pump();
        Assert.Equal("Reader view is missing from this build (reader folder).", model.Notice);
    });

    [Fact]
    public void ATabMovedToChromiumFromGeckosReaderOpensTheArticle() => TestLoop.Run(async () =>
    {
        using var fixture = Create();
        var model = fixture.Model;
        await model.Productivity.StartAsync();
        var tab = fixture.OpenGeckoTab(Article);
        fixture.Gecko.Report(Tab(tab.GeckoId!.Value, "about:reader?url=" + Uri.EscapeDataString(Article), selected: true));
        model.SetEngine(EngineKind.Chromium);
        await TestLoop.Settle();
        Assert.Equal(Article, tab.Url);
        Assert.Equal(EngineKind.Chromium, tab.Engine);
    });

    [Fact]
    public void OriginalUrlOfAReaderPage()
    {
        Assert.Equal("https://a.example/x?y=1", ReaderMode.OriginalUrl("about:reader?url=" + Uri.EscapeDataString("https://a.example/x?y=1")));
        Assert.Null(ReaderMode.OriginalUrl("https://a.example/"));
    }
}
