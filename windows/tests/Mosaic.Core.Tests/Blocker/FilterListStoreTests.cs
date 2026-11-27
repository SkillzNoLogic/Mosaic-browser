using System.Net;
using System.Text;
using Mosaic.Core.Blocker;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Blocker;

/// <summary>
/// Port of <c>FilterListStoreTests</c> in <c>Tests/MosaicCoreTests/BlockerTests.swift</c>, followed
/// by tests of the update, which the Swift suite does not cover.
/// </summary>
public sealed class FilterListStoreTests : IDisposable
{
    private static readonly string Filler = string.Concat(Enumerable.Repeat("||ads.example^\n", 200));

    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void CatalogueMatchesUBlockOriginsDefaults()
    {
        var ids = FilterListCatalog.Defaults.Select(list => list.Id).ToList();
        Assert.Equal(
            ["ublock-filters", "ublock-badware", "ublock-privacy", "ublock-unbreak", "ublock-quick-fixes",
             "easylist", "easyprivacy", "urlhaus-1", "plowe-0"],
            ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(FilterListCatalog.Defaults, list => Assert.Equal("https", list.Url.Scheme));
    }

    [Fact]
    public void RecognisesListsAndRejectsErrorPages()
    {
        Assert.True(FilterListStore.LooksValid(Bytes($"[Adblock Plus 2.0]\n! Title: X\n{Filler}"), isSuffixList: false));
        Assert.True(FilterListStore.LooksValid(Bytes($"! Title: EasyList\n{Filler}"), isSuffixList: false));
        Assert.True(FilterListStore.LooksValid(Bytes($"# hosts\n{Filler}"), isSuffixList: false));
        Assert.False(FilterListStore.LooksValid(Bytes($"<!DOCTYPE html><html>{Filler}"), isSuffixList: false));
        Assert.False(FilterListStore.LooksValid(Bytes("! too short"), isSuffixList: false));
        Assert.True(FilterListStore.LooksValid(Bytes($"// This Source Code Form… publicsuffix.org\n{Filler}"), isSuffixList: true));
        Assert.False(FilterListStore.LooksValid(Bytes($"! Title: X\n{Filler}"), isSuffixList: true));
    }

    [Fact]
    public void SeedsMissingFilesOnlyAndReadsThemBack()
    {
        var bundled = Directory.CreateDirectory(root.File("bundled")).FullName;
        var lists = FilterListCatalog.Defaults.Take(2).ToList();
        File.WriteAllText(Path.Combine(bundled, FilterListCatalog.SuffixListFileName), "psl");
        File.WriteAllText(Path.Combine(bundled, lists[0].FileName), "! first");
        File.WriteAllText(Path.Combine(bundled, lists[1].FileName), "! second");

        var store = new FilterListStore(root.File("lists"), lists);
        Directory.CreateDirectory(store.Directory);
        // A list that is already there (say, a newer download) must survive seeding.
        File.WriteAllText(store.PathFor(lists[1]), "! newer");

        store.Seed(bundled);

        Assert.Equal("psl", store.SuffixList());
        Assert.Equal(["! first", "! newer"], store.LoadLists().Select(list => list.Text));
        Assert.Equal(["ublock-filters", "ublock-badware"], store.LoadLists().Select(list => list.Name));
        Assert.Equal(2, store.AvailableListPaths().Count);
        Assert.NotNull(store.OldestModification());
    }

    [Fact]
    public void MissingListsAreSkipped()
    {
        var store = new FilterListStore(root.Path);
        Assert.Empty(store.LoadLists());
        Assert.Null(store.SuffixList());
        Assert.Null(store.OldestModification());
    }

    // ---- Updating ----

    /// <summary>Answers each URL with whatever the test put in <see cref="Responses"/>; anything else is a 404.</summary>
    private sealed class ListServer : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Responses { get; } = [];
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var respond)
                ? Task.FromResult(respond())
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public void Serve(Uri url, string text, HttpStatusCode status = HttpStatusCode.OK) =>
            Responses[url.AbsoluteUri] = () => new HttpResponseMessage(status) { Content = new StringContent(text) };
    }

    [Fact]
    public async Task UpdateReplacesWhatDownloadedWellAndKeepsTheRest()
    {
        var lists = FilterListCatalog.Defaults.Take(4).ToList();
        var store = new FilterListStore(root.File("lists"), lists);
        Directory.CreateDirectory(store.Directory);
        foreach (var list in lists) File.WriteAllText(store.PathFor(list), $"! old {list.Id}");
        File.WriteAllText(store.SuffixListPath, "old psl");

        var server = new ListServer();
        server.Serve(FilterListCatalog.SuffixListUrl, $"// publicsuffix.org\n{Filler}");
        server.Serve(lists[0].Url, $"! Title: fresh\n{Filler}");
        server.Serve(lists[1].Url, $"<!DOCTYPE html><title>Rate limited</title>{Filler}");  // an error page
        server.Serve(lists[2].Url, $"! Title: fresh\n{Filler}", HttpStatusCode.InternalServerError);
        server.Responses[lists[3].Url.AbsoluteUri] = () => throw new HttpRequestException("offline");

        var result = await store.UpdateAsync(server);

        Assert.Equal([FilterListCatalog.SuffixListFileName, lists[0].Id], result.Updated);
        Assert.Equal([lists[1].Id, lists[2].Id, lists[3].Id], result.Failed);
        Assert.Equal(5, server.Requests.Count);
        Assert.StartsWith("// publicsuffix.org", store.SuffixList());
        Assert.StartsWith("! Title: fresh", File.ReadAllText(store.PathFor(lists[0])));
        foreach (var kept in lists.Skip(1)) Assert.Equal($"! old {kept.Id}", File.ReadAllText(store.PathFor(kept)));
        Assert.Empty(Directory.GetFiles(store.Directory, "*.part"));
    }

    [Fact]
    public async Task UpdateCreatesTheFolderAndFillsAnEmptyStore()
    {
        var lists = FilterListCatalog.Defaults.Take(1).ToList();
        var store = new FilterListStore(root.File("new/lists"), lists);
        var server = new ListServer();
        server.Serve(FilterListCatalog.SuffixListUrl, $"===BEGIN ICANN DOMAINS===\n{Filler}");
        server.Serve(lists[0].Url, $"[Adblock Plus 2.0]\n{Filler}");

        var result = await store.UpdateAsync(server);

        Assert.Empty(result.Failed);
        Assert.Equal(2, result.Updated.Count);
        Assert.Single(store.LoadLists());
        Assert.NotNull(store.OldestModification());
    }

    [Fact]
    public async Task OldestModificationIsTheLeastRecentlyWrittenFile()
    {
        var lists = FilterListCatalog.Defaults.Take(2).ToList();
        var store = new FilterListStore(root.Path, lists);
        File.WriteAllText(store.SuffixListPath, "psl");
        foreach (var list in lists) File.WriteAllText(store.PathFor(list), "! list");
        var old = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(store.PathFor(lists[1]), old);

        Assert.Equal(new DateTimeOffset(old), store.OldestModification());

        // A refresh of everything moves it forward.
        var server = new ListServer();
        server.Serve(FilterListCatalog.SuffixListUrl, $"publicsuffix.org\n{Filler}");
        foreach (var list in lists) server.Serve(list.Url, $"! fresh\n{Filler}");
        await store.UpdateAsync(server);
        Assert.True(store.OldestModification() > new DateTimeOffset(old).AddDays(1));
    }
}
