using Mosaic.Core.Blocker;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Blocker;

/// <summary>
/// Port of <c>CosmeticFilteringTests</c> in <c>Tests/MosaicCoreTests/BlockerTests.swift</c>: cosmetic
/// filtering, filter details and validation on uBO's real parser (skipped without the bundle), and
/// the user's filters, optional lists and options files (always run).
/// </summary>
public sealed class CosmeticFilteringTests : IDisposable
{
    private const string Filters = """
        ! Title: Mosaic cosmetic test list
        ||ads.example^
        ##.ad-banner
        ##div[id^="sponsor-"]
        ###top-ad
        example.com##.promo
        example.com#@#.ad-banner
        news.example.com##.newsletter
        ~shop.example.com,example.com##.sidebar-ad
        example.*##.entity-ad
        #@#.globally-excepted
        ##.globally-excepted
        example.com##.styled:style(opacity: 0.5 !important)
        example.com##.text:has-text(Sponsored)
        example.com##+js(nobab)
        other.org##.promo
        """;

    private const string NetworkFilters = """
        ! Title: Mosaic test list
        ||ads.example^
        ||tracker.example^$third-party
        /banner/*$image
        @@||ads.example/allowed.js$script
        example.org##.ad
        """;

    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    private static BlockerEngine MakeEngine(string filters = Filters) =>
        new(UBlockBundle.BundlePath, File.ReadAllText(UBlockBundle.SuffixListPath), [new FilterListText("test", filters)]);

    [UBlockFact]
    public void NetworkEngineIsUnaffected()
    {
        using var engine = MakeEngine();
        Assert.Equal(1, engine.FilterCount);
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://ads.example/x.js", "https://a.com/", "script"));
        Assert.True(engine.SupportsCosmetics);
    }

    [UBlockFact]
    public void GenericStylesheet()
    {
        using var engine = MakeEngine();
        var css = engine.GenericCosmeticCss();
        var rules = css.Split('\n');
        Assert.Contains(".ad-banner{display:none!important;}", rules);
        Assert.Contains("div[id^=\"sponsor-\"]{display:none!important;}", rules);
        Assert.Contains("#top-ad{display:none!important;}", rules);
        Assert.DoesNotContain("globally-excepted", css);   // excepted everywhere by #@#
        Assert.DoesNotContain(".promo", css);              // site-specific filters are not generic
        Assert.DoesNotContain(".ad-banner", engine.GenericCosmeticCss([".ad-banner"]));
        Assert.Equal(4, engine.CosmeticStats.Generic);
        Assert.True(engine.CosmeticStats.Unsupported >= 2); // :has-text and +js
    }

    [UBlockFact]
    public void SiteSpecificRules()
    {
        using var engine = MakeEngine();
        var example = engine.CosmeticRulesFor("www.example.com");
        Assert.Contains(".promo", example.Css);
        Assert.Contains(".sidebar-ad", example.Css);
        Assert.Contains(".entity-ad", example.Css);                       // example.* entity filter
        Assert.Contains(".styled\n{opacity: 0.5 !important}", example.Css); // cssable :style()
        Assert.DoesNotContain("Sponsored", example.Css);                  // procedural: not applied
        Assert.DoesNotContain(".newsletter", example.Css);                // other subdomain only
        Assert.Equal([".ad-banner"], example.GenericExceptions);          // example.com#@#.ad-banner

        var news = engine.CosmeticRulesFor("news.example.com");
        Assert.Contains(".newsletter", news.Css);
        Assert.Contains(".promo", news.Css);

        var shop = engine.CosmeticRulesFor("shop.example.com");
        Assert.DoesNotContain(".sidebar-ad", shop.Css);                   // ~shop.example.com
        Assert.Contains(".promo", shop.Css);

        var unrelated = engine.CosmeticRulesFor("unrelated.net");
        Assert.Equal("", unrelated.Css);
        Assert.Empty(unrelated.GenericExceptions);
    }

    [UBlockFact]
    public void StyleCacheCombinesGenericAndSpecific()
    {
        using var engine = MakeEngine();
        var cache = new CosmeticStyleCache(engine);
        var page = cache.StylesheetFor("example.com", includeGeneric: true);
        Assert.Contains("#top-ad", page);
        Assert.Contains(".promo", page);
        Assert.DoesNotContain(".ad-banner", page);                        // the site's generic exception
        var frame = cache.StylesheetFor("other.org", includeGeneric: false);
        Assert.Contains(".promo", frame);
        Assert.DoesNotContain("#top-ad", frame);
        Assert.Equal(cache.RulesFor("EXAMPLE.com"), cache.RulesFor("example.com"));
    }

    [UBlockFact]
    public void RequestFilterBuildsFrameScriptsAndLogsBlocks()
    {
        var filter = new RequestFilter();
        using var engine = MakeEngine();
        filter.Use(engine);
        var page = filter.CosmeticScript("https://example.com/", "https://example.com/", isMainFrame: true);
        Assert.NotNull(page);
        Assert.Contains("#top-ad", page);
        // A third-party frame gets only its own site's rules.
        var frame = filter.CosmeticScript("https://other.org/frame", "https://example.com/", isMainFrame: false);
        Assert.NotNull(frame);
        Assert.DoesNotContain("#top-ad", frame);
        Assert.Null(filter.CosmeticScript("https://unrelated.net/frame", "https://example.com/", isMainFrame: false));

        filter.CosmeticsEnabled = false;
        Assert.Null(filter.CosmeticScript("https://example.com/", "https://example.com/", isMainFrame: true));
        filter.CosmeticsEnabled = true;
        filter.Use(new BlockerSettings(allowlist: ["example.com"]));
        Assert.Null(filter.CosmeticScript("https://example.com/", "https://example.com/", isMainFrame: true));
        filter.Use(new BlockerSettings());

        Assert.True(filter.ShouldBlock("https://ads.example/x.js", "https://site.example/", "script"));
        Assert.Single(filter.Log.EntriesFor("https://site.example/"));
        // A new document on the page starts its log over.
        filter.CosmeticScript("https://site.example/next", "https://site.example/next", isMainFrame: true);
        Assert.Empty(filter.Log.EntriesFor("https://site.example/"));
        filter.Use((BlockerEngine?)null);
    }

    [UBlockFact]
    public void DetailsNameTheFilter()
    {
        using var engine = MakeEngine(NetworkFilters);
        const string page = "https://news.example.com/";
        var blocked = engine.Details("https://ads.example/x.js", page, "script");
        Assert.Equal(BlockerVerdict.Block, blocked.Verdict);
        Assert.Equal("||ads.example^", blocked.Filter);
        var allowed = engine.Details("https://ads.example/allowed.js", page, "script");
        Assert.Equal(BlockerVerdict.Allow, allowed.Verdict);
        Assert.StartsWith("@@||ads.example/allowed.js", allowed.Filter);
        Assert.Contains("/banner/", engine.Details("https://cdn.example.net/banner/1.png", page, "image").Filter);
        Assert.Equal("", engine.Details("https://ok.example/", page, "script").Filter);
    }

    [UBlockFact]
    public void ValidatesUserFilters()
    {
        using var engine = MakeEngine();
        var checks = engine.Validate("""
            ! my filters
            ||tracker.example^$third-party
            example.com##.cookie-banner

            ##.everywhere > .ad
            ||x.example^$bogus
            example.com##+js(set-constant, x, 1)
            example.com##div:has-text(Ad)
            ##[unclosed
            ||y.example^$removeparam=utm_source
            ||z.example^$redirect=noopjs
            """);
        var byLine = checks.ToDictionary(check => check.Line);
        Assert.Equal(FilterCheckKind.Comment, byLine[1].Kind);
        Assert.Equal(FilterCheckKind.Network, byLine[2].Kind);
        Assert.Equal(FilterCheckKind.Cosmetic, byLine[3].Kind);
        Assert.False(byLine.ContainsKey(4));                              // blank lines are skipped
        Assert.Equal(FilterCheckKind.Cosmetic, byLine[5].Kind);
        Assert.Equal(FilterCheckKind.Error, byLine[6].Kind);
        Assert.Contains("bogus", byLine[6].Message);
        Assert.Equal(FilterCheckKind.Unsupported, byLine[7].Kind);
        Assert.Equal(FilterCheckKind.Unsupported, byLine[8].Kind);
        Assert.Equal(FilterCheckKind.Error, byLine[9].Kind);
        Assert.Equal(FilterCheckKind.Unsupported, byLine[10].Kind);
        Assert.Equal(FilterCheckKind.Network, byLine[11].Kind);
        Assert.NotEqual("", byLine[11].Message);                          // blocks instead of redirecting
        Assert.Equal(4, checks.Count(check => check.IsEffective));
    }

    [UBlockFact]
    public void UserFiltersTakeEffectInTheEngine()
    {
        var store = new FilterListStore(root.File("lists"), []);
        store.SaveUserFilters("||mine.example^\nexample.com##.mine");
        using var engine = new BlockerEngine(UBlockBundle.BundlePath, File.ReadAllText(UBlockBundle.SuffixListPath), store.LoadLists());
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://mine.example/a.js", "https://a.com/", "script"));
        Assert.Contains(".mine", engine.CosmeticRulesFor("example.com").Css);
    }

    [Fact]
    public void UserFiltersAreLoadedAfterTheLists()
    {
        var lists = FilterListCatalog.Defaults.Take(1).ToList();
        var store = new FilterListStore(root.File("lists"), lists);
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.PathFor(lists[0]), "! list");
        Assert.Equal([lists[0].Id], store.LoadLists().Select(list => list.Name));
        store.SaveUserFilters("||mine.example^\nexample.com##.mine");
        Assert.Equal([lists[0].Id, FilterListCatalog.UserFiltersName], store.LoadLists().Select(list => list.Name));
        Assert.Equal(store.UserFiltersPath, store.AvailableListPaths()[^1]);
        Assert.Equal("My filters", store.ListContaining("||mine.example^"));
        Assert.Null(store.ListContaining("||nothing.example^"));
        store.SaveUserFilters("");
        Assert.Single(store.LoadLists());
    }

    [Fact]
    public void ListContainingFindsTheListByTitle()
    {
        var lists = FilterListCatalog.Defaults.Take(2).ToList();
        var store = new FilterListStore(root.File("lists"), lists);
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.PathFor(lists[1]), "! list\n||ads.example^\r\n");
        Assert.Equal(lists[1].Title, store.ListContaining("ads.example"));
    }

    [Fact]
    public void ListsCanBeSwitchedAndMissingOnesAreReported()
    {
        var store = new FilterListStore(root.File("lists"), FilterListCatalog.Defaults.Take(1).ToList());
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.PathFor(FilterListCatalog.Defaults[0]), "! list");
        Assert.Empty(store.MissingLists());
        var cookies = FilterListCatalog.Optional.First(list => list.Id == "fanboy-cookiemonster");
        store.UseLists([FilterListCatalog.Defaults[0], cookies]);
        Assert.Equal([cookies], store.MissingLists());
        Assert.False(store.IsDownloaded(cookies));
        Assert.Null(store.OldestModification());
    }

    [Fact]
    public void CatalogueOffersStricterListsWithoutClashes()
    {
        var ids = FilterListCatalog.All.Select(list => list.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(FilterListCatalog.Optional, list => list.Id == "fanboy-cookiemonster" && list.Group == "annoyances");
        Assert.All(FilterListCatalog.Optional, list => Assert.True(list.Url.Scheme == "https" && list.Summary.Length > 0));
        Assert.Equal(["easylist", "adguard-cookies"], FilterListCatalog.ListsEnabled(["adguard-cookies", "easylist", "unknown"]).Select(list => list.Id));
        Assert.All(FilterListCatalog.All, list => Assert.Contains(FilterListCatalog.Groups, group => group.Id == list.Group));
    }

    [Fact]
    public void UpdateMessages()
    {
        Assert.Equal("All lists are up to date.", new FilterListUpdate(["easylist"], []).Message);
        Assert.Equal("Could not download: EasyList, mystery.", new FilterListUpdate([], ["easylist", "mystery"]).Message);
    }

    [Fact]
    public void OptionsPersistWithMacOSNames()
    {
        var path = root.File("Blocker/options.json");
        Assert.Equal(new BlockerOptions(), BlockerOptions.Load(path));
        var options = new BlockerOptions { CosmeticEnabled = false }.WithList("adguard-cookies", true).WithList("plowe-0", false);
        options.Save(path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"blockerCosmetic\": false", text);
        Assert.Contains("\"adguard-cookies\"", text);
        var loaded = BlockerOptions.Load(path);
        Assert.Equal(options, loaded);
        Assert.Contains(loaded.EnabledLists, list => list.Id == "adguard-cookies");
        Assert.DoesNotContain(loaded.EnabledLists, list => list.Id == "plowe-0");

        File.WriteAllText(path, """{"blockerLists":["easylist","gone-list"]}""");
        Assert.Equal(["easylist"], BlockerOptions.Load(path).EnabledListIds);
        File.WriteAllText(path, "{broken");
        Assert.Equal(new BlockerOptions(), BlockerOptions.Load(path));
    }
}
