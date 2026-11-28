using System.Text.Json.Nodes;
using Mosaic.Core.Library;

namespace Mosaic.Core.Tests.Library;

/// <summary>Port of <c>NetscapeBookmarksTests</c> and <c>ChromeBookmarksTests</c>, plus Chrome JSON export.</summary>
public class NetscapeBookmarksTests
{
    internal const string Sample = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><H3 ADD_DATE="1700000000" PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
            <DL><p>
                <DT><A HREF="https://swift.org/" ADD_DATE="1700000001" TAGS="lang,apple">Swift &amp; Co</A>
                <DT><H3>Docs</H3>
                <DL><p>
                    <DT><A href='https://developer.apple.com/documentation/' add_date=1700000002>Apple Docs</A>
                    <DD>A description that is ignored
                </DL><p>
            </DL><p>
            <DT><A HREF="https://example.com/?a=1&amp;b=2">Example &#8212; &quot;quoted&quot;</A>
            <dt><h3>Empty</h3>
            <dl><p>
            </dl><p>
        </DL><p>
        """;

    [Fact]
    public void ParsesFoldersToolbarTagsAndEntities()
    {
        var items = NetscapeBookmarks.Parse(Sample);
        Assert.Equal(3, items.Count);
        var bar = items[0];
        Assert.True(bar.IsToolbar);
        Assert.Equal("Bookmarks bar", bar.Title);
        Assert.Equal(["Swift & Co", "Docs"], bar.Children.Select(c => c.Title));
        Assert.Equal(["lang", "apple"], bar.Children[0].Tags);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_001), bar.Children[0].Added);
        Assert.Equal(["https://developer.apple.com/documentation/"], bar.Children[1].Children.Select(c => c.Url));
        Assert.Equal("https://example.com/?a=1&b=2", items[1].Url);
        Assert.Equal("Example — \"quoted\"", items[1].Title);
        Assert.True(items[2].IsFolder);
        Assert.Empty(items[2].Children);
        Assert.Equal(3, items.Sum(i => i.BookmarkCount));
    }

    [Fact]
    public void ImportsIntoBarAndOtherAndIsIdempotent()
    {
        using var store = new LibraryStore(null);
        var items = NetscapeBookmarks.Parse(Sample);
        Assert.Equal(3, store.ImportBookmarks(items));
        Assert.Equal(["Swift & Co", "Docs"], store.Children(Bookmark.BarId).Select(b => b.Title));
        Assert.Equal(["Example — \"quoted\"", "Empty"], store.Children(Bookmark.OtherId).Select(b => b.Title));
        Assert.Equal(0, store.ImportBookmarks(items));
        Assert.Equal(3, store.BookmarkCount());
    }

    internal static LibraryStore Populated()
    {
        var store = new LibraryStore(null);
        store.AddBookmark("https://a.test/?x=<1>&y=\"2\"", "A <&> \"B\"", Bookmark.BarId, ["one", "two"]);
        var folder = store.AddFolder("Folder & Stuff", Bookmark.BarId);
        store.AddBookmark("https://b.test/", "B", folder.Id);
        var nested = store.AddFolder("Nested", Bookmark.OtherId);
        store.AddBookmark("https://c.test/", "Ünïcödé ✓", nested.Id);
        store.AddBookmark("https://d.test/", "", Bookmark.OtherId);
        return store;
    }

    internal static List<string> Shape(LibraryStore store, string folder, bool tags = true) =>
        [.. store.Children(folder).SelectMany(item =>
        {
            var line = $"{(item.IsFolder ? "folder" : "url")}|{item.Title}|{item.Url}|{(tags ? string.Join(",", item.Tags) : "")}";
            return item.IsFolder ? [line, .. Shape(store, item.Id, tags).Select(child => "  " + child)] : new[] { line };
        })];

    [Fact]
    public void ExportThenImportRoundTrips()
    {
        using var store = Populated();
        var html = NetscapeBookmarks.Export(store.AllBookmarks());
        using var copy = new LibraryStore(null);
        Assert.Equal(4, copy.ImportBookmarks(NetscapeBookmarks.Parse(html)));
        Assert.Equal(Shape(store, Bookmark.BarId), Shape(copy, Bookmark.BarId));
        Assert.Equal(Shape(store, Bookmark.OtherId), Shape(copy, Bookmark.OtherId));
        var original = store.AllBookmarks().First(b => b.Url == "https://b.test/");
        var imported = copy.AllBookmarks().First(b => b.Url == "https://b.test/");
        Assert.Equal(original.Created.ToUnixTimeSeconds(), imported.Created.ToUnixTimeSeconds());
    }

    [Fact]
    public void ExportStartsWithTheNetscapeHeaderAndMarksTheToolbar()
    {
        using var store = Populated();
        var html = NetscapeBookmarks.Export(store.AllBookmarks());
        Assert.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n", html);
        Assert.Contains("PERSONAL_TOOLBAR_FOLDER=\"true\">Bookmarks Bar</H3>", html);
        Assert.Contains("TAGS=\"one,two\"", html);
        Assert.Contains("&lt;&amp;&gt; &quot;B&quot;", html);
    }

    [Fact]
    public void ToleratesMillisecondAndMicrosecondDatesAndJunk()
    {
        var items = NetscapeBookmarks.Parse("<DL><DT><A HREF=\"https://a.test/\" ADD_DATE=\"1700000000000\">A</A><DT><A HREF=\"https://b.test/\" ADD_DATE=\"1700000000000000\">B</A><DT><A HREF=\"\">none</A><DT><A HREF=\"https://c.test/\" ADD_DATE=\"x\">C");
        Assert.Equal(["A", "B", ""], items.Select(i => i.Title));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), items[0].Added);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), items[1].Added);
        Assert.Null(items[2].Added);
    }

    [Fact]
    public void EntityDecoding()
    {
        Assert.Equal("a & b <c> \"d\" 'e'   — \U0001F600 &bogus; &", NetscapeBookmarks.DecodeEntities(
            "a &amp; b &lt;c&gt; &quot;d&quot; &apos;e&apos; &nbsp; &#x2014; &#128512; &bogus; &"));
    }
}

public class ChromeBookmarksTests
{
    private const string Json = """
        {"roots": {
          "bookmark_bar": {"type": "folder", "name": "Bookmarks bar", "children": [
            {"type": "url", "name": "Swift", "url": "https://swift.org/", "date_added": "13345000000000000"},
            {"type": "folder", "name": "Work", "children": [
              {"type": "url", "name": "Mail", "url": "https://mail.test/"}]}]},
          "other": {"type": "folder", "name": "Other bookmarks", "children": [
            {"type": "url", "name": "Example", "url": "https://example.com/"}]},
          "synced": {"type": "folder", "name": "Mobile bookmarks", "children": []}
        }, "version": 1}
        """;

    [Fact]
    public void ParsesRootsAndDates()
    {
        var items = ChromeBookmarks.Parse(Json)!;
        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsToolbar);
        Assert.Equal(["Swift", "Work"], items[0].Children.Select(c => c.Title));
        Assert.Equal("https://mail.test/", items[0].Children[1].Children[0].Url);
        Assert.Equal("https://example.com/", items[1].Url);
        var added = items[0].Children[0].Added!.Value;
        Assert.True(Math.Abs(added.ToUnixTimeSeconds() - (13_345_000_000 - 11_644_473_600)) < 1);

        using var store = new LibraryStore(null);
        Assert.Equal(3, store.ImportBookmarks(items));
        Assert.Equal(["Swift", "Work"], store.Children(Bookmark.BarId).Select(b => b.Title));
    }

    [Fact]
    public void RejectsOtherJson()
    {
        Assert.Null(ChromeBookmarks.Parse("{\"a\":1}"));
        Assert.Null(ChromeBookmarks.Parse("nope"));
        Assert.Null(ChromeBookmarks.Parse("[1,2]"));
    }

    [Fact]
    public void ExportThenImportRoundTrips()
    {
        using var store = NetscapeBookmarksTests.Populated();
        var json = ChromeBookmarks.Export(store.AllBookmarks());

        var document = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(1, document["version"]!.GetValue<int>());
        Assert.Equal("Bookmarks bar", document["roots"]!["bookmark_bar"]!["name"]!.GetValue<string>());
        var ids = new List<string>();
        void Collect(JsonNode node)
        {
            ids.Add(node["id"]!.GetValue<string>());
            foreach (var child in node["children"]?.AsArray() ?? []) Collect(child!);
        }
        foreach (var root in document["roots"]!.AsObject()) Collect(root.Value!);
        Assert.Equal(ids.Count, ids.Distinct().Count());

        using var copy = new LibraryStore(null);
        Assert.Equal(4, copy.ImportBookmarks(ChromeBookmarks.Parse(json)!));
        // Chrome's format has no tags; everything else survives.
        Assert.Equal(NetscapeBookmarksTests.Shape(store, Bookmark.BarId, tags: false), NetscapeBookmarksTests.Shape(copy, Bookmark.BarId, tags: false));
        Assert.Equal(NetscapeBookmarksTests.Shape(store, Bookmark.OtherId, tags: false), NetscapeBookmarksTests.Shape(copy, Bookmark.OtherId, tags: false));
        var original = store.AllBookmarks().First(b => b.Url == "https://c.test/");
        var imported = copy.AllBookmarks().First(b => b.Url == "https://c.test/");
        Assert.Equal(original.Created.ToUnixTimeMilliseconds(), imported.Created.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ReadBookmarkFileTellsTheFormatsApart()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("Bookmarks"), Json);
        File.WriteAllText(folder.File("bookmarks.html"), NetscapeBookmarksTests.Sample);
        File.WriteAllText(folder.File("notes.txt"), "just some text");
        Assert.Equal(2, BrowserLibrary.ReadBookmarkFile(folder.File("Bookmarks")).Count);
        Assert.Equal(3, BrowserLibrary.ReadBookmarkFile(folder.File("bookmarks.html")).Count);
        Assert.Throws<LibraryException>(() => BrowserLibrary.ReadBookmarkFile(folder.File("notes.txt")));
        Assert.Throws<LibraryException>(() => BrowserLibrary.ReadBookmarkFile(folder.File("missing.html")));
    }
}
