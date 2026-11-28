using System.Net;
using Mosaic.Core.Browser;
using Mosaic.Core.Library;
using Mosaic.Core.Search;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Library;

/// <summary>The library wired to a browser model with fake engines: history from both engines, keywords, suggestions.</summary>
public class BrowserLibraryTests
{
    private static async Task<IReadOnlyList<HistoryVisit>> VisitsAsync(BrowserModel model)
    {
        await model.Library.Idle();
        return await model.Library.VisitsAsync();
    }

    /// <summary>Lets a page's title settle so its visit is committed, and waits for the write.</summary>
    private static async Task SettleAsync(BrowserFixture fixture)
    {
        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(2));
        await fixture.Model.Library.Idle();
        await TestLoop.Settle();
    }

    [Fact]
    public void ConstructingTheModelDoesNotCreateOrOpenTheLibrary() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var field = typeof(BrowserModel).GetField("library", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Null(field.GetValue(fixture.Model));
        Assert.False(fixture.Model.Library.IsLoaded);
    });

    [Fact]
    public void AGeckoNavigationIsOneTypedVisitWithItsTitle() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        model.Navigate("example.com");
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com", "", loading: true, selected: true));
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com", "Example Domain", selected: true));
        fixture.Gecko.Report(Snapshots.Tab(1, "https://example.com", "Example Domain", selected: true));
        await SettleAsync(fixture);

        var visit = Assert.Single(await VisitsAsync(model));
        Assert.Equal("https://example.com", visit.Url);
        Assert.Equal("Example Domain", visit.Title);
        Assert.True(visit.Typed);
        Assert.Equal(new HistoryCounts(1, 1), model.Library.HistoryCounts);
    });

    [Fact]
    public void BackgroundGeckoTabsAreRecordedToo() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var first = fixture.OpenGeckoTab("https://one.test/");
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(10));
        var second = fixture.OpenGeckoTab("https://two.test/");
        Assert.Same(second, model.SelectedTab);
        // The page in the background tab follows a link on its own.
        fixture.Gecko.Report(
            Snapshots.Tab(first.GeckoId!.Value, "https://one.test/next", "Next page"),
            Snapshots.Tab(second.GeckoId!.Value, "https://two.test/", "Two", selected: true));
        await SettleAsync(fixture);

        var urls = (await VisitsAsync(model)).Select(v => v.Url).Order(StringComparer.Ordinal);
        Assert.Equal(["https://one.test/next", "https://two.test/"], urls);
    });

    [Fact]
    public void ChromiumNavigationsAreRecorded() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (_, browser) = fixture.OpenChromiumTab("https://chromium.test/");
        browser.RaiseLoadState(loading: true);
        browser.RaiseUrl("https://chromium.test/");
        browser.RaiseTitle("Chromium page");
        browser.RaiseLoadState(loading: false);
        await SettleAsync(fixture);
        browser.RaiseUrl("https://chromium.test/second");
        browser.RaiseTitle("Second");
        await SettleAsync(fixture);

        Assert.Equal(["Second", "Chromium page"], (await VisitsAsync(model)).Select(v => v.Title));
    });

    [Fact]
    public void PrivateTabsAndInternalPagesAreNeverRecorded() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        model.Library.IsPrivateTab = tab => tab.Url.Contains("secret", StringComparison.Ordinal);
        var (_, browser) = fixture.OpenChromiumTab("https://secret.test/");
        browser.RaiseTitle("Secret");
        browser.RaiseLoadState(loading: false);
        // Mosaic's own pages are shown by the app, with no engine behind them (winui M1).
        var internalPage = model.NewTab("mosaic://history", EngineKind.Chromium);
        Assert.Null(internalPage.Chromium);
        await SettleAsync(fixture);
        Assert.Empty(await VisitsAsync(model));
    });

    [Fact]
    public void SearchKeywordsGoToTheirEngine() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        model.Navigate("w mosaic browser");
        Assert.Equal("https://en.wikipedia.org/wiki/Special:Search?search=mosaic%20browser", model.Tabs[0].Url);
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(10));
        model.Navigate("@ddg privacy");
        Assert.StartsWith("https://duckduckgo.com/?q=privacy", model.Tabs[0].Url);

        // An engine added in settings works at once.
        await model.Library.SaveEngineAsync(new CustomSearchEngine { Id = "custom.mdn", Name = "MDN", Keyword = "mdn", Template = "https://developer.mozilla.org/search?q=%s" });
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(10));
        model.Navigate("mdn fetch");
        Assert.Equal("https://developer.mozilla.org/search?q=fetch", model.Tabs[0].Url);
        Assert.False(model.Library.IsKeywordAvailable("MDN", null));
        Assert.True(model.Library.IsKeywordAvailable("mdn", "custom.mdn"));
        Assert.False(model.Library.IsKeywordAvailable("@google", null));
    });

    [Fact]
    public void ACustomDefaultSearchEngineIsRestoredOnceTheLibraryIsRead() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture(f => File.WriteAllText(f.SettingsPath, """{"searchEngine": "custom.wikipedia"}"""));
        var model = fixture.Model;
        await model.Library.WhenLoaded;
        await TestLoop.Settle();
        Assert.Equal("custom.wikipedia", model.SearchEngine.Id);
    });

    [Fact]
    public void LocalSuggestionsRankHistoryBookmarksAndOpenTabs() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        await model.Library.RecordVisitAsync("https://github.com/", "GitHub", typed: true);
        await model.Library.RecordVisitAsync("https://example.com/digital", "Digital");
        await model.Library.AddBookmarkAsync("https://gitlab.com/", "GitLab");
        fixture.OpenGeckoTab("https://gitea.test/");
        model.NewTab();
        await fixture.Time.AdvanceAsync(TimeSpan.FromSeconds(2));
        await model.Library.Idle();
        await TestLoop.Settle();

        var rows = new List<IReadOnlyList<LibrarySuggestion>>();
        await foreach (var list in model.Library.SuggestionStream("git")) rows.Add(list);
        Assert.Equal(2, rows.Count);  // local, then with all of history; no remote request while off
        var final = rows[^1];
        // A host-prefix match goes above the search row: the typed visit outranks the bookmark.
        Assert.Equal("https://github.com/", final[0].Url);
        Assert.Equal(SuggestionKind.Search, final[1].Kind);
        Assert.Equal("Search Google", final[1].Detail);
        Assert.Contains(final, row => row is { Kind: SuggestionKind.Bookmark, Url: "https://gitlab.com/", IsBookmarked: true });
        Assert.Contains(final, row => row is { Kind: SuggestionKind.OpenTab, Detail: "Switch to tab" });
        Assert.True(model.Library.IsBookmarked("https://GitLab.com"));

        var keywordRows = model.Library.Suggestions("w swift");
        Assert.Equal("Search Wikipedia", keywordRows[0].Detail);
        Assert.Empty(model.Library.Suggestions("   "));
    });

    [Fact]
    public void OpeningASuggestionSwitchesToItsTabOrLoadsIt() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var open = fixture.OpenGeckoTab("https://open.test/");
        var blank = model.NewTab();
        var tabRow = model.Library.Suggestions("open.test").First(row => row.Kind == SuggestionKind.OpenTab);
        model.Library.Open(tabRow);
        Assert.Same(open, model.SelectedTab);

        model.Select(blank);
        await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(10));
        model.Library.Open(new LibrarySuggestion("x", "https://x.test/", SuggestionKind.History) { Url = "https://x.test/" });
        Assert.Equal("https://x.test/", blank.Url);
        model.Library.Open(new LibrarySuggestion("y", "https://y.test/", SuggestionKind.History) { Url = "https://y.test/" }, inNewTab: true);
        Assert.Equal("https://y.test/", model.SelectedTab!.Url);
    });

    private sealed class SuggestServer : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""["swi",["swift","swift ui","switch"]]"""),
            });
        }
    }

    [Fact]
    public void RemoteSuggestionsOnlyWhenSwitchedOnAndNeverForPrivateTabs() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var server = new SuggestServer();
        model.UseLibrary(new LibraryOptions { SuggestionHandler = server });
        Assert.False(model.Library.RemoteSuggestionsEnabled);

        async Task<IReadOnlyList<LibrarySuggestion>> Final(string input)
        {
            IReadOnlyList<LibrarySuggestion> last = [];
            var stream = model.Library.SuggestionStream(input).GetAsyncEnumerator();
            while (true)
            {
                var next = stream.MoveNextAsync().AsTask();
                // The pause before the request is on the model's clock.
                while (!next.IsCompleted) await fixture.Time.AdvanceAsync(TimeSpan.FromMilliseconds(100));
                if (!await next) break;
                last = stream.Current;
            }
            return last;
        }

        Assert.DoesNotContain(await Final("swi"), row => row.IsRemote);
        Assert.Empty(server.Requests);

        model.Library.RemoteSuggestionsEnabled = true;
        var rows = await Final("swi");
        Assert.Equal(["swift", "swift ui", "switch"], rows.Where(row => row.IsRemote).Select(row => row.Title));
        Assert.Equal("Google suggestion", rows.First(row => row.IsRemote).Detail);
        Assert.Equal("https://suggestqueries.google.com/complete/search?client=firefox&q=swi", Assert.Single(server.Requests).AbsoluteUri);

        // Addresses are never sent, and nothing is sent from a private tab.
        await Final("example.com");
        model.Library.IsPrivateTab = _ => true;
        await Final("swi");
        Assert.Single(server.Requests);
    });

    [Fact]
    public void BookmarksFoldersAndTheBar() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var library = fixture.Model.Library;
        Assert.True(await library.ToggleBookmarkAsync("https://a.test/", "A"));
        var folder = (await library.AddFolderAsync("Work", Bookmark.BarId))!;
        var mail = (await library.AddBookmarkAsync("https://mail.test/", "", folder.Id, ["work", "Work", "mail"]))!;
        Assert.Equal("mail.test", mail.Title);
        Assert.Equal(["A", "Work"], library.BookmarksBar.Select(b => b.Title));
        Assert.Equal(["mail", "work"], library.AllTags.Select(t => t.ToLowerInvariant()));
        Assert.Equal([mail.Id], library.BookmarksTagged("WORK").Select(b => b.Id));
        Assert.Equal([(Bookmark.BarId, 0), (folder.Id, 1), (Bookmark.OtherId, 0)], library.FolderTree().Select(f => (f.Folder.Id, f.Depth)));

        await library.UpdateBookmarkAsync(mail.Id, "Mail", null, null, Bookmark.OtherId);
        Assert.Equal(Bookmark.OtherId, library.BookmarkById(mail.Id)!.ParentId);
        Assert.Equal(Bookmark.OtherId, library.DefaultFolderId);
        Assert.Equal(["Mail"], (await library.SearchBookmarksAsync("mail")).Select(b => b.Title));

        // A failing change is reported, and the state stays consistent.
        await library.MoveBookmarkAsync(folder.Id, folder.Id);
        Assert.Equal("A folder cannot go inside itself", library.LastError);
        Assert.Contains("A folder cannot go inside itself", fixture.Model.Notice);

        Assert.False(await library.ToggleBookmarkAsync("https://a.test/", "A"));
        await library.DeleteBookmarksAsync([folder.Id]);
        Assert.Empty(library.BookmarksBar);
        Assert.False(await library.ToggleBookmarkAsync("mosaic://history", "History"));
    });

    [Fact]
    public void ImportAndExportThroughTheLibrary() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        using var folder = new TempFolder();
        var library = fixture.Model.Library;
        File.WriteAllText(folder.File("in.html"), NetscapeBookmarksTests.Sample);
        Assert.Equal(3, await library.ImportBookmarksAsync(folder.File("in.html")));
        Assert.Equal(0, await library.ImportBookmarksAsync(folder.File("in.html")));
        await library.ExportBookmarksAsync(folder.File("out.html"));
        await library.ExportBookmarksAsync(folder.File("out.json"), BookmarkFileFormat.ChromeJson);
        Assert.Equal(3, NetscapeBookmarks.Parse(File.ReadAllText(folder.File("out.html"))).Sum(b => b.BookmarkCount));
        Assert.Equal(3, ChromeBookmarks.Parse(File.ReadAllText(folder.File("out.json")))!.Sum(b => b.BookmarkCount));
        File.WriteAllText(folder.File("junk.txt"), "hello");
        await Assert.ThrowsAsync<LibraryException>(() => library.ImportBookmarksAsync(folder.File("junk.txt")));
    });

    [Fact]
    public void HistoryDeletionAndClearing() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var library = fixture.Model.Library;
        await library.RecordVisitAsync("https://a.example.com/", "A");
        await library.RecordVisitAsync("https://b.test/", "B");
        await library.RecordVisitAsync("https://b.test/", "B");
        await library.RecordVisitAsync("https://private.test/", "P", isPrivate: true);
        Assert.Equal(new HistoryCounts(2, 3), library.HistoryCounts);

        var visits = await library.VisitsAsync();
        await library.DeleteVisitsAsync([visits.First(v => v.Url == "https://b.test/").Id]);
        Assert.Equal(new HistoryCounts(2, 2), library.HistoryCounts);
        await library.DeleteHistorySiteAsync("example.com");
        Assert.Equal(new HistoryCounts(1, 1), library.HistoryCounts);
        var revision = library.HistoryRevision;
        await library.ClearHistoryAsync(LibraryClearRange.LastHour);
        Assert.Equal(new HistoryCounts(0, 0), library.HistoryCounts);
        Assert.True(library.HistoryRevision > revision);
    });

    [Fact]
    public void ReadingListAndPreferencesPersistInTheDatabase() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        using var folder = new TempFolder();
        var path = folder.File(LibraryStore.FileName);
        var model = fixture.Model;
        model.UseLibrary(new LibraryOptions { DatabasePath = path });
        await model.Library.WhenLoaded;
        await model.Library.AddToReadingListAsync("https://read.test/", "Read me");
        await model.Library.AddToReadingListAsync("mosaic://history", "no");
        Assert.True(model.Library.IsInReadingList("https://read.test/"));
        await model.Library.SetReadAsync(model.Library.ReadingList[0], true);
        Assert.True(model.Library.ReadingList[0].IsRead);
        model.Library.ShowsBookmarksBar = false;
        model.Library.RemoteSuggestionsEnabled = true;
        await model.Library.Idle();

        model.UseLibrary(new LibraryOptions { DatabasePath = path });
        Assert.True(model.Library.ShowsBookmarksBar);  // a default until the file is read
        await model.Library.WhenLoaded;
        await TestLoop.Settle();
        Assert.False(model.Library.ShowsBookmarksBar);
        Assert.True(model.Library.RemoteSuggestionsEnabled);
        Assert.Single(model.Library.ReadingList);
        Assert.Null(model.Library.StorageProblem);
        await model.Library.RemoveFromReadingListAsync(model.Library.ReadingList[0]);
        Assert.Empty(model.Library.ReadingList);
    });

    [Fact]
    public void AnUnopenableDatabaseFallsBackToMemoryAndSaysSo() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        using var folder = new TempFolder();
        var path = folder.File("broken.sqlite");
        File.WriteAllText(path, new string('x', 4096));
        fixture.Model.UseLibrary(new LibraryOptions { DatabasePath = path });
        await fixture.Model.Library.WhenLoaded;
        await TestLoop.Settle();
        Assert.Contains("could not open its library database", fixture.Model.Library.StorageProblem);
        Assert.NotNull(await fixture.Model.Library.AddBookmarkAsync("https://a.test/", "A"));
    });

    [Fact]
    public void ShutdownWritesVisitsStillWaitingForTheirTitle() => TestLoop.Run(async () =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (_, browser) = fixture.OpenChromiumTab("https://late.test/");
        browser.RaiseTitle("Late");
        browser.RaiseLoadState(loading: false);
        Assert.Equal(1, model.Library.DumpState()["pendingVisits"]!.GetValue<int>());
        model.Shutdown();
        Assert.Equal(["https://late.test/"], (await VisitsAsync(model)).Select(v => v.Url));
    });

    [Fact]
    public void LibraryPagesOpenOnceAndReuseTheirTab() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        Assert.Equal("reading-list", LibraryPage.ReadingList.Host());
        Assert.Equal(LibraryPage.History, LibraryPages.FromHost("History"));
        fixture.OpenGeckoTab("https://a.test/");
        var count = model.Tabs.Count;
        model.OpenLibraryPage(LibraryPage.History);
        Assert.Equal(count + 1, model.Tabs.Count);
        var page = model.SelectedTab!;
        Assert.Equal("mosaic://history", page.Url);
        model.Select(model.Tabs[0]);
        model.OpenLibraryPage(LibraryPage.History);
        Assert.Same(page, model.SelectedTab);
        Assert.Equal(count + 1, model.Tabs.Count);
    });
}
