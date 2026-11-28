using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Library;

/// <summary>The library's automation commands (macOS names) and its <c>dump</c> key, through the pipe's handler.</summary>
public class LibraryAutomationTests
{
    private static async Task<JsonObject> RunAsync(Automation automation, string command, string value = "", string? output = null)
    {
        var request = new JsonObject { ["do"] = command, ["value"] = value };
        if (output is not null) request["out"] = output;
        var reply = await automation.HandleAsync(request.ToJsonString());
        return JsonNode.Parse(reply.Json)!.AsObject();
    }

    private static async Task OkAsync(Automation automation, string command, string value = "", string? output = null)
    {
        var reply = await RunAsync(automation, command, value, output);
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
    }

    [Fact]
    public void CommandsChangeTheLibraryAndDumpShowsIt() => TestLoop.Run(async () =>
    {
        LibraryAutomation.Register();
        LibraryAutomation.Register();  // once only
        using var fixture = new BrowserFixture();
        using var folder = new TempFolder();
        var automation = new Automation(fixture.Model, _ => Task.CompletedTask);
        fixture.OpenGeckoTab("https://example.com/");

        var bookmarked = await RunAsync(automation, "libraryBookmark");
        Assert.Equal("https://example.com/", bookmarked["result"]!["url"]!.GetValue<string>());
        await OkAsync(automation, "libraryReadingAdd");
        await OkAsync(automation, "libraryRecordVisit", "https://visited.test/|Visited");
        await OkAsync(automation, "libraryAddEngine", "MDN|mdn|https://developer.mozilla.org/search?q=%s");
        await OkAsync(automation, "libraryBar", "hide");
        await OkAsync(automation, "libraryRemoteSuggestions", "off");

        var state = fixture.Model.Library.DumpState();
        var dump = (await RunAsync(automation, "dump"))["state"]!["library"]!;
        Assert.True(dump["loaded"]!.GetValue<bool>());
        Assert.Equal(1, dump["bookmarks"]!.GetValue<int>());
        Assert.Equal("example.com", dump["bookmarksBar"]![0]!.GetValue<string>());
        Assert.False(dump["showsBookmarksBar"]!.GetValue<bool>());
        Assert.True(dump["selectedIsBookmarked"]!.GetValue<bool>());
        Assert.Equal(1, dump["readingList"]!.GetValue<int>());
        Assert.Equal(1, dump["historyVisits"]!.GetValue<int>());
        Assert.Equal(["w=Wikipedia", "mdn=MDN"], dump["customEngines"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Equal(state.ToJsonString(), dump.ToJsonString());

        var path = folder.File("bookmarks.json");
        await OkAsync(automation, "libraryBookmarks", path);
        // Folders (the two roots) and bookmarks, as on macOS.
        Assert.Equal(["folder", "folder", "url"], JsonNode.Parse(File.ReadAllText(path))!.AsArray().Select(b => b!["kind"]!.GetValue<string>()).Order());
        var history = await RunAsync(automation, "libraryHistory");
        Assert.Equal("https://visited.test/", history["result"]!["latest"]![0]!["url"]!.GetValue<string>());
        var suggest = await RunAsync(automation, "librarySuggest", "visited");
        Assert.Contains(suggest["result"]!["final"]!.AsArray(), row => row!["url"]!.GetValue<string>() == "https://visited.test/");

        await OkAsync(automation, "libraryExport", folder.File("out.html"));
        await OkAsync(automation, "libraryExport", folder.File("out.json"));
        Assert.StartsWith("<!DOCTYPE NETSCAPE", File.ReadAllText(folder.File("out.html")));
        Assert.NotNull(ChromeBookmarks.Parse(File.ReadAllText(folder.File("out.json"))));
        File.WriteAllText(folder.File("in.html"), NetscapeBookmarksTests.Sample);
        var imported = await RunAsync(automation, "libraryImport", folder.File("in.html"));
        Assert.Equal(3, imported["result"]!["added"]!.GetValue<int>());

        await OkAsync(automation, "libraryClearHistory", "all");
        Assert.Equal(0, fixture.Model.Library.HistoryCounts.Visits);
        await OkAsync(automation, "libraryOpen", "reading-list");
        Assert.Equal("mosaic://reading-list", fixture.Model.SelectedTab!.Url);
    });

    [Fact]
    public void BadInputIsReported() => TestLoop.Run(async () =>
    {
        LibraryAutomation.Register();
        using var fixture = new BrowserFixture();
        using var folder = new TempFolder();
        var automation = new Automation(fixture.Model, _ => Task.CompletedTask);
        Assert.False((await RunAsync(automation, "libraryBookmark"))["ok"]!.GetValue<bool>());  // the start page
        Assert.False((await RunAsync(automation, "libraryAddEngine", "X|w|https://x.test/?q=%s"))["ok"]!.GetValue<bool>());
        Assert.False((await RunAsync(automation, "libraryAddEngine", "X|x|https://x.test/"))["ok"]!.GetValue<bool>());
        Assert.False((await RunAsync(automation, "libraryOpen", "downloads"))["ok"]!.GetValue<bool>());
        File.WriteAllText(folder.File("junk.txt"), "junk");
        var reply = await RunAsync(automation, "libraryImport", folder.File("junk.txt"));
        Assert.Contains("format Mosaic can read", reply["error"]!.GetValue<string>());
    });

    [Fact]
    public void GeckoShortcutEventsReachTheLibrary() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var tab = fixture.OpenGeckoTab("https://page.test/");
        var editorRequests = new List<(string Id, bool IsNew)>();
        model.Library.BookmarkEditorRequested += (id, isNew) => editorRequests.Add((id, isNew));

        fixture.Gecko.ReportEvents([Snapshots.Tab(tab.GeckoId!.Value, "https://page.test/", "Page", selected: true)], "bookmarkPage");
        await model.Library.WhenLoaded;
        await model.Library.Idle();
        await TestLoop.Settle();
        Assert.True(model.Library.IsBookmarked("https://page.test/"));
        Assert.Equal("Bookmarked in Bookmarks Bar", model.Notice);
        Assert.Equal([true], editorRequests.Select(r => r.IsNew));  // focus changes are allowed outside automation

        // Each snapshot reports the Gecko tabs as they are, plus the event.
        void Send(string type) => fixture.Gecko.ReportEvents(
            [.. model.Tabs.Where(t => t.GeckoId is not null).Select(t => Snapshots.Tab(t.GeckoId!.Value, t.Url, t.Title, selected: ReferenceEquals(t, model.SelectedTab)))],
            type);
        Send("toggleBookmarksBar");
        Assert.False(model.Library.ShowsBookmarksBar);
        Send("showHistory");
        Assert.Equal("mosaic://history", model.SelectedTab!.Url);
        Send("showBookmarks");
        Assert.Equal("mosaic://bookmarks", model.SelectedTab!.Url);
    });
}
