using Mosaic.Core.Library;

namespace Mosaic.Core.Tests.Library;

/// <summary>Port of the store suites in <c>Tests/MosaicCoreTests/LibraryTests.swift</c>.</summary>
public class LibraryStoreSchemaTests
{
    [Fact]
    public void FreshDatabaseHasRootsAndCurrentVersion()
    {
        using var store = new LibraryStore(null);
        Assert.Equal(LibraryStore.SchemaVersion, store.UserVersion);
        var roots = store.AllBookmarks().Where(b => b.ParentId is null).Select(b => b.Id).Order(StringComparer.Ordinal);
        Assert.Equal([Bookmark.BarId, Bookmark.OtherId], roots);
        Assert.Equal(["w"], store.CustomEngines().Select(e => e.Keyword));
    }

    [Fact]
    public void ReopeningAFileKeepsDataAndDoesNotMigrateTwice()
    {
        using var folder = new TempFolder();
        var file = folder.File(LibraryStore.FileName);
        using (var store = new LibraryStore(file))
        {
            store.AddBookmark("https://example.com/", "Example");
            store.RecordVisit("https://example.com/", "Example");
        }
        using var reopened = new LibraryStore(file);
        Assert.Equal(1, reopened.BookmarkCount());
        Assert.Equal(1, reopened.HistoryCounts().Visits);
        Assert.Equal(2, reopened.AllBookmarks().Count(b => b.ParentId is null));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void AnUnusableFileFailsWithALibraryException()
    {
        using var folder = new TempFolder();
        var file = folder.File("not-a-database.sqlite");
        File.WriteAllText(file, new string('x', 4096));
        Assert.Throws<LibraryException>(() => new LibraryStore(file));
    }

    [Fact]
    public void PlaceholdersAreNumberedOutsideStringLiterals()
    {
        Assert.Equal("a = ?1 AND b LIKE ?2 ESCAPE '?'", SqliteDatabase.NumberPlaceholders("a = ? AND b LIKE ? ESCAPE '?'", out var count));
        Assert.Equal(2, count);
        Assert.Equal("x = ?1 OR y = ?3", SqliteDatabase.NumberPlaceholders("x = ?1 OR y = ?3", out count));
        Assert.Equal(3, count);
    }
}

public class BookmarkStoreTests
{
    [Fact]
    public void AddFindAndRemoveByUrl()
    {
        using var store = new LibraryStore(null);
        store.AddBookmark("https://Example.com/#top", "Example", Bookmark.BarId);
        Assert.Single(store.BookmarksForUrl("https://example.com"));
        Assert.Contains(LibraryUrl.Normalized("https://example.com/"), store.BookmarkedUrlKeys());
        Assert.Equal(1, store.RemoveBookmarksForUrl("https://example.com/"));
        Assert.Equal(0, store.BookmarkCount());
    }

    [Fact]
    public void PositionsStayContiguous()
    {
        using var store = new LibraryStore(null);
        var a = store.AddBookmark("https://a.test/", "A", Bookmark.BarId);
        var b = store.AddBookmark("https://b.test/", "B", Bookmark.BarId);
        var c = store.AddBookmark("https://c.test/", "C", Bookmark.BarId, position: 0);
        Assert.Equal([c.Id, a.Id, b.Id], store.Children(Bookmark.BarId).Select(x => x.Id));
        Assert.Equal([0, 1, 2], store.Children(Bookmark.BarId).Select(x => x.Position));

        store.MoveBookmark(c.Id, Bookmark.BarId, 2);
        Assert.Equal([a.Id, b.Id, c.Id], store.Children(Bookmark.BarId).Select(x => x.Id));

        store.DeleteBookmark(a.Id);
        Assert.Equal([0, 1], store.Children(Bookmark.BarId).Select(x => x.Position));
    }

    [Fact]
    public void MovingBetweenFoldersAndCycleProtection()
    {
        using var store = new LibraryStore(null);
        var outer = store.AddFolder("Outer");
        var inner = store.AddFolder("Inner", outer.Id);
        var page = store.AddBookmark("https://x.test/", "X", Bookmark.BarId);

        store.MoveBookmark(page.Id, inner.Id);
        Assert.Equal([page.Id], store.Children(inner.Id).Select(x => x.Id));
        Assert.Empty(store.Children(Bookmark.BarId));

        Assert.Throws<LibraryException>(() => store.MoveBookmark(outer.Id, inner.Id));
        Assert.Throws<LibraryException>(() => store.MoveBookmark(Bookmark.BarId, outer.Id));

        // Deleting a folder deletes what is inside it.
        store.DeleteBookmark(outer.Id);
        Assert.Null(store.Bookmark(page.Id));
        Assert.Equal(0, store.BookmarkCount());
    }

    [Fact]
    public void EditingTitleUrlAndTags()
    {
        using var store = new LibraryStore(null);
        var page = store.AddBookmark("https://old.test/", "Old", tags: ["Swift", "swift", " ui "]);
        Assert.Equal(["Swift", "ui"], store.Bookmark(page.Id)!.Tags);
        store.UpdateBookmark(page.Id, "New", "https://new.test/", ["docs"]);
        var updated = store.Bookmark(page.Id)!;
        Assert.Equal("New", updated.Title);
        Assert.Equal("https://new.test/", updated.Url);
        Assert.Equal(["docs"], updated.Tags);
        Assert.Empty(store.BookmarksForUrl("https://old.test/"));
        Assert.Equal(["docs"], store.AllTags());
    }

    [Fact]
    public void SearchMatchesTitleUrlAndTags()
    {
        using var store = new LibraryStore(null);
        store.AddBookmark("https://swift.org/", "Swift", tags: ["language"]);
        store.AddBookmark("https://developer.apple.com/", "Apple Developer", tags: ["docs"]);
        Assert.Equal(["Swift"], store.SearchBookmarks("swift").Select(b => b.Title));
        Assert.Equal(["Apple Developer"], store.SearchBookmarks("docs").Select(b => b.Title));
        Assert.Single(store.SearchBookmarks("apple developer"));
        Assert.Empty(store.SearchBookmarks("100%"));
        Assert.Empty(store.SearchBookmarks("swift_"));
    }

    [Fact]
    public void AddingToAMissingFolderFails()
    {
        using var store = new LibraryStore(null);
        Assert.Throws<LibraryException>(() => store.AddBookmark("https://a.test/", "A", "nope"));
        var page = store.AddBookmark("https://a.test/", "A");
        Assert.Throws<LibraryException>(() => store.AddBookmark("https://b.test/", "B", page.Id));
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void VisitsAggregateAndTitlesUpdate()
    {
        using var store = new LibraryStore(null);
        store.RecordVisit("https://example.com/", "", typed: true);
        store.RecordVisit("https://example.com/", "Example Domain");
        var entry = store.HistoryEntry("https://example.com/")!;
        Assert.Equal(2, entry.VisitCount);
        Assert.Equal(1, entry.TypedCount);
        Assert.Equal("Example Domain", entry.Title);
        Assert.Equal("example.com", entry.Host);
        store.UpdateTitle("https://example.com/", "Renamed");
        Assert.Equal("Renamed", store.HistoryEntry("https://example.com/")!.Title);
    }

    [Fact]
    public void InternalAndBlankPagesAreNotRecorded()
    {
        using var store = new LibraryStore(null);
        foreach (var url in (string[])["mosaic://history", "about:blank", "data:text/html,hi", "chrome-error://x", ""])
        {
            store.RecordVisit(url, "x");
        }
        Assert.Equal(0, store.HistoryCounts().Addresses);
    }

    [Fact]
    public void ClearByTimeRange()
    {
        using var store = new LibraryStore(null);
        var now = DateTimeOffset.UtcNow;
        store.RecordVisit("https://old.test/", "Old", at: now.AddDays(-3));
        store.RecordVisit("https://both.test/", "Both", at: now.AddDays(-3));
        store.RecordVisit("https://both.test/", "Both", at: now.AddSeconds(-60));
        store.RecordVisit("https://new.test/", "New", at: now.AddSeconds(-30));

        store.ClearHistory(now.AddHours(-1));
        Assert.Null(store.HistoryEntry("https://new.test/"));
        var both = store.HistoryEntry("https://both.test/")!;
        Assert.Equal(1, both.VisitCount);
        Assert.True(both.LastVisit < now.AddDays(-1));
        Assert.NotNull(store.HistoryEntry("https://old.test/"));

        store.ClearHistory(null);
        Assert.Equal(new HistoryCounts(0, 0), store.HistoryCounts());
    }

    [Fact]
    public void DeleteBySiteCoversSubdomainsOnly()
    {
        using var store = new LibraryStore(null);
        store.RecordVisit("https://example.com/a", "");
        store.RecordVisit("https://www.example.com/b", "");
        store.RecordVisit("https://docs.example.com/c", "");
        store.RecordVisit("https://notexample.com/", "");
        Assert.Equal(3, store.DeleteHistorySite("example.com"));
        Assert.Equal(["https://notexample.com/"], store.RecentHistory(10).Select(e => e.Url));
    }

    [Fact]
    public void DeletingAVisitUpdatesCounts()
    {
        using var store = new LibraryStore(null);
        store.RecordVisit("https://a.test/", "A");
        store.RecordVisit("https://a.test/", "A");
        var visits = store.Visits();
        Assert.Equal(2, visits.Count);
        store.DeleteVisits([visits[0].Id]);
        Assert.Equal(1, store.HistoryEntry("https://a.test/")!.VisitCount);
        store.DeleteVisits([visits[1].Id]);
        Assert.Null(store.HistoryEntry("https://a.test/"));
    }

    [Fact]
    public void VisitSearchAndPaging()
    {
        using var store = new LibraryStore(null);
        var start = DateTimeOffset.UtcNow.AddSeconds(-1000);
        for (var i = 0; i < 10; i++) store.RecordVisit($"https://site{i}.test/", $"Page {i}", at: start.AddSeconds(i));
        var first = store.Visits(limit: 4);
        Assert.Equal(["Page 9", "Page 8", "Page 7", "Page 6"], first.Select(v => v.Title));
        var next = store.Visits(before: first[^1].Time, limit: 4);
        Assert.Equal("Page 5", next[0].Title);
        Assert.Equal(["https://site3.test/"], store.Visits("page 3").Select(v => v.Url));
        Assert.Single(store.SearchHistory("site7", 5));
        Assert.Equal(["Page 9", "Page 8"], store.Visits(since: start.AddSeconds(8)).Select(v => v.Title));
    }

    [Fact]
    public void TopSitesAreOnePerHost()
    {
        using var store = new LibraryStore(null);
        for (var i = 0; i < 5; i++) store.RecordVisit("https://github.com/", "GitHub");
        for (var i = 0; i < 3; i++) store.RecordVisit("https://github.com/apple", "Apple");
        store.RecordVisit("https://swift.org/", "Swift");
        Assert.Equal(["https://github.com/", "https://swift.org/"], store.TopSites(5).Select(e => e.Url));
    }

    [Fact]
    public async Task ConcurrentUseIsSafe()
    {
        using var store = new LibraryStore(null);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 50; j++)
            {
                store.RecordVisit($"https://t{i}.test/{j % 5}", "T");
                _ = store.SearchHistory($"t{i}", 5);
            }
        })));
        Assert.Equal(new HistoryCounts(40, 400), store.HistoryCounts());
    }
}

public class ReadingListStoreTests
{
    [Fact]
    public void AddMarkReadRemove()
    {
        using var store = new LibraryStore(null);
        var a = store.AddToReadingList("https://a.test/", "A", DateTimeOffset.UtcNow.AddSeconds(-10));
        var b = store.AddToReadingList("https://b.test/", "B");
        Assert.Equal([b.Id, a.Id], store.ReadingList().Select(i => i.Id));
        store.SetRead(b.Id, true);
        Assert.Equal([a.Id, b.Id], store.ReadingList().Select(i => i.Id));
        Assert.True(store.ReadingList()[^1].IsRead);
        // Adding again marks it unread and keeps one entry.
        store.AddToReadingList("https://b.test/", "");
        Assert.Equal(2, store.ReadingList().Count);
        Assert.Equal("B", store.ReadingList()[0].Title);
        Assert.All(store.ReadingList(), item => Assert.False(item.IsRead));
        store.RemoveFromReadingList(a.Id);
        Assert.Equal(["https://b.test/"], store.ReadingList().Select(i => i.Url));
        Assert.True(store.IsInReadingList("https://b.test/"));
    }
}

public class CustomEngineStoreTests
{
    [Fact]
    public void SaveReplaceAndDelete()
    {
        using var store = new LibraryStore(null);
        var engine = new CustomSearchEngine { Id = "custom.x", Name = "X", Keyword = "x", Template = "https://x.test/?q=%s" };
        store.SaveCustomEngine(engine);
        store.SaveCustomEngine(engine with { Name = "Ex" });
        Assert.Equal(["Wikipedia", "Ex"], store.CustomEngines().Select(e => e.Name));
        store.DeleteCustomEngine("custom.x");
        Assert.Equal(["Wikipedia"], store.CustomEngines().Select(e => e.Name));
    }

    [Fact]
    public void MetaValuesRoundTrip()
    {
        using var store = new LibraryStore(null);
        Assert.Null(store.Meta("pref.x"));
        store.SetMeta("pref.x", "1");
        store.SetMeta("pref.x", "2");
        Assert.Equal("2", store.Meta("pref.x"));
        store.SetMeta("pref.x", null);
        Assert.Null(store.Meta("pref.x"));
    }
}

public class LibraryUrlTests
{
    [Fact]
    public void Helpers()
    {
        Assert.Equal("example.com", LibraryUrl.Display("https://www.example.com/"));
        Assert.Equal("example.com/a/", LibraryUrl.Display("http://example.com/a/"));
        Assert.Equal("example.com", LibraryUrl.Host("https://WWW.Example.com/x"));
        Assert.Equal("example.com", LibraryUrl.Host("https://user@example.com:8080/x"));
        Assert.Equal("", LibraryUrl.Host("not a url"));
        Assert.Equal(LibraryUrl.Normalized("https://example.com"), LibraryUrl.Normalized("HTTPS://Example.COM/#frag"));
        Assert.NotEqual(LibraryUrl.Normalized("https://example.com/b"), LibraryUrl.Normalized("https://example.com/a"));
        Assert.Equal("https://example.com?q=1", LibraryUrl.Normalized("https://Example.com/?q=1#x"));
        Assert.Equal("https://example.com/Path", LibraryUrl.Normalized("https://example.com/Path"));
        Assert.True(LibraryUrl.IsRecordable("https://a.test/"));
        Assert.True(LibraryUrl.IsRecordable("file:///C:/a.html"));
        Assert.False(LibraryUrl.IsRecordable("mosaic://bookmarks"));
        Assert.False(LibraryUrl.IsRecordable("about:blank"));
        Assert.False(LibraryUrl.IsRecordable("https://a b"));
    }
}
