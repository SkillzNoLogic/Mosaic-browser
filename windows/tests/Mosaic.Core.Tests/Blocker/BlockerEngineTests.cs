using System.Diagnostics;
using Mosaic.Core.Blocker;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Blocker;

/// <summary>
/// Port of <c>BlockerEngineTests</c> in <c>Tests/MosaicCoreTests/BlockerTests.swift</c>: runs uBlock
/// Origin's real engine, built by <c>scripts/bootstrap.ps1</c> (see <see cref="UBlockBundle"/>), and
/// is skipped where it is absent. Then the real engine on the real list snapshot, and tests of the
/// hosting itself against <see cref="FakeBundle"/>, which always run.
/// </summary>
public sealed class BlockerEngineTests : IDisposable
{
    private const string Filters = """
        ! Title: Mosaic test list
        ||ads.example^
        ||tracker.example^$third-party
        /banner/*$image
        @@||ads.example/allowed.js$script
        example.org##.ad
        """;

    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    private static BlockerEngine MakeEngine() =>
        new(UBlockBundle.BundlePath, File.ReadAllText(UBlockBundle.SuffixListPath), [new FilterListText("test", Filters)]);

    [UBlockFact]
    public void CompilesNetworkFiltersOnly()
    {
        using var engine = MakeEngine();
        // Four network filters; the cosmetic one (##.ad) is not compiled.
        Assert.Equal(4, engine.FilterCount);
        Assert.Contains(".", engine.Version);
    }

    [UBlockFact]
    public void DecidesLikeUBlockOrigin()
    {
        using var engine = MakeEngine();
        const string page = "https://news.example.com/article";
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://ads.example/x.js", page, "script"));
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://sub.ads.example/p.gif", page, "image"));
        Assert.Equal(BlockerVerdict.Allow, engine.Verdict("https://ads.example/allowed.js", page, "script"));
        Assert.Equal(BlockerVerdict.None, engine.Verdict("https://cdn.example.net/app.js", page, "script"));
        // $image applies to images only.
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://cdn.example.net/banner/1.png", page, "image"));
        Assert.Equal(BlockerVerdict.None, engine.Verdict("https://cdn.example.net/banner/1.js", page, "script"));
        // $third-party: blocked from other sites, allowed on the tracker's own site.
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://tracker.example/t.js", page, "script"));
        Assert.Equal(BlockerVerdict.None, engine.Verdict("https://tracker.example/t.js", "https://www.tracker.example/", "script"));
    }

    [UBlockFact]
    public async Task IsSafeToQueryFromManyThreads()
    {
        using var engine = MakeEngine();
        var verdicts = await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(() => engine.Verdict(
            index % 2 == 0 ? $"https://ads.example/{index}.js" : $"https://ok.example/{index}.js",
            "https://site.example/", "script"))));
        Assert.Equal(100, verdicts.Count(verdict => verdict == BlockerVerdict.Block));
        Assert.Equal(100, verdicts.Count(verdict => verdict == BlockerVerdict.None));
    }

    [UBlockFact]
    public void RequestFilterAppliesSettings()
    {
        var filter = new RequestFilter();
        (string Url, string Page, string Type) ad = ("https://ads.example/x.js", "https://site.example/page", "script");
        Assert.False(filter.ShouldBlock(ad.Url, ad.Page, ad.Type)); // no engine yet

        using var engine = MakeEngine();
        Assert.Null(filter.Use(engine));
        Assert.True(filter.ShouldBlock(ad.Url, ad.Page, ad.Type));
        Assert.False(filter.ShouldBlock(ad.Url, "about:blank", ad.Type));

        filter.Use(new BlockerSettings(allowlist: ["site.example"]));
        Assert.False(filter.ShouldBlock(ad.Url, ad.Page, ad.Type));

        filter.Use(new BlockerSettings(isEnabled: false));
        Assert.False(filter.ShouldBlock(ad.Url, ad.Page, ad.Type));
    }

    [Fact]
    public void AMissingBundleIsReported()
    {
        var error = Assert.Throws<BlockerEngineException>(() =>
            new BlockerEngine(root.File("nonexistent/ublock-engine.js"), "", []));
        Assert.Contains("could not be read", error.Message);
    }

    // ---- The real engine on the shipped list snapshot ----

    [UBlockFact]
    public void CompilesTheListSnapshotAndBlocksWhatItShould()
    {
        var store = new FilterListStore(UBlockBundle.ListsDirectory);
        var lists = store.LoadLists();
        Assert.Equal(FilterListCatalog.Defaults.Count, lists.Count);

        var clock = Stopwatch.StartNew();
        using var engine = new BlockerEngine(UBlockBundle.BundlePath, store.SuffixList()!, lists);
        var compile = clock.Elapsed;

        // uBO's default lists hold a quarter of a million network filters; far fewer means lists went missing.
        Assert.InRange(engine.FilterCount, 100_000, 1_000_000);
        Assert.InRange(compile, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        const string page = "https://www.example.com/";
        Assert.Equal(BlockerVerdict.Block,
            engine.Verdict("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js", page, "script"));
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://www.google-analytics.com/analytics.js", page, "script"));
        Assert.Equal(BlockerVerdict.None, engine.Verdict("https://www.example.com/app.js", page, "script"));
        Assert.Equal(BlockerVerdict.None, engine.Verdict("https://cdnjs.cloudflare.com/ajax/libs/jquery/3.7.1/jquery.min.js", page, "script"));

        // The handler that asks this runs on the UI thread for every request: it has to be quick.
        // About 4 µs on an Apple Silicon Mac; the bound leaves room for a slow CI machine, not for
        // a call path ten times slower (see Verdict).
        const int rounds = 2_000;
        void Ask(int index) => engine.Verdict($"https://cdn{index % 7}.example.net/assets/{index}/app.js?v={index}", page, "script");
        for (var index = 0; index < 200; index++) Ask(index);
        clock.Restart();
        for (var index = 0; index < rounds; index++) Ask(index);
        Assert.InRange(clock.Elapsed / rounds, TimeSpan.Zero, TimeSpan.FromMicroseconds(50));
    }

    // ---- Hosting, against the stand-in bundle ----

    private string FakeBundlePath() => FakeBundle.WriteTo(root.File("bundle"));

    [Fact]
    public void LoadsABundleAndMarshalsCallsToIt()
    {
        using var engine = new BlockerEngine(FakeBundlePath(), "psl",
            [new FilterListText("one", "! one\n||a^\n||b^"), new FilterListText("two", "||c^\nexample.org##.ad")]);

        Assert.Equal("0.0-test", engine.Version);
        Assert.Equal(3, engine.FilterCount);
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://ads.example/x.js", "https://site.example/", "script"));
        Assert.Equal(BlockerVerdict.Allow, engine.Verdict("https://ads.example/allowed.js", "https://site.example/", "script"));
        Assert.Equal(BlockerVerdict.None, engine.Verdict("https://cdn.example/x.js", "https://site.example/", "script"));
    }

    [Fact]
    public void AScriptErrorWhileLoadingIsReported()
    {
        var error = Assert.Throws<BlockerEngineException>(() =>
            new BlockerEngine(FakeBundlePath(), "psl", [new FilterListText("broken", "THROW")]));
        Assert.Contains("bad list broken", error.Message);

        File.WriteAllText(root.File("empty.js"), "var Something = 1;");
        error = Assert.Throws<BlockerEngineException>(() => new BlockerEngine(root.File("empty.js"), "psl", []));
        Assert.Contains("did not define MosaicBlocker", error.Message);

        File.WriteAllText(root.File("syntax.js"), "var MosaicBlocker = {");
        Assert.Throws<BlockerEngineException>(() => new BlockerEngine(root.File("syntax.js"), "psl", []));
    }

    [Fact]
    public void AScriptErrorWhileMatchingLetsTheRequestThrough()
    {
        using var engine = new BlockerEngine(FakeBundlePath(), "psl", []);
        Assert.Equal(BlockerVerdict.None, engine.Verdict("https://ads.example/throw.js", "https://site.example/", "script"));
        // And the engine carries on.
        Assert.Equal(BlockerVerdict.Block, engine.Verdict("https://ads.example/x.js", "https://site.example/", "script"));
    }

    [Fact]
    public async Task DisposingWhileOtherThreadsAskIsSafe()
    {
        var engine = new BlockerEngine(FakeBundlePath(), "psl", []);
        var filter = new RequestFilter();
        filter.Use(engine);
        using var stop = new CancellationTokenSource();
        var blocked = 0;
        var passed = 0;

        var askers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var block = filter.ShouldBlock("https://ads.example/x.js", "https://site.example/", "script");
                Interlocked.Increment(ref block ? ref blocked : ref passed);
            }
        })).ToList();

        await Eventually.TrueAsync(() => Volatile.Read(ref blocked) > 1_000);
        // Disposed while still in use, as a replaced engine is when a request is in flight.
        engine.Dispose();
        var passedAtDisposal = Volatile.Read(ref passed);
        await Eventually.TrueAsync(() => Volatile.Read(ref passed) > passedAtDisposal + 1_000);
        engine.Dispose(); // twice is harmless
        await stop.CancelAsync();
        await Task.WhenAll(askers); // and none of them failed
        Assert.Equal(BlockerVerdict.None, engine.Verdict("https://ads.example/x.js", "https://site.example/", "script"));
    }
}
