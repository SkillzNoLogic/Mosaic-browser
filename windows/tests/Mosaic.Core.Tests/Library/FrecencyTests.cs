using Mosaic.Core.Library;

namespace Mosaic.Core.Tests.Library;

/// <summary>Port of <c>FrecencyTests</c> and <c>SuggestionRankerTests</c>.</summary>
public class FrecencyTests
{
    [Fact]
    public void RecentAndTypedVisitsScoreHigher()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(100, Frecency.Score(1, [(now, false)], now));
        Assert.Equal(10, Frecency.Score(1, [(now.AddDays(-200), false)], now));
        Assert.Equal(200, Frecency.Score(1, [(now, true)], now));
        Assert.Equal(0, Frecency.Score(0, [], now));
    }

    [Fact]
    public void FrequentBeatsOccasional()
    {
        var now = DateTimeOffset.UtcNow;
        var visits = Enumerable.Range(0, 5).Select(i => (now.AddDays(-6 * i), false)).ToList();
        Assert.True(Frecency.Score(20, visits, now) > Frecency.Score(2, visits.Take(2), now));
    }

    [Fact]
    public void StoreKeepsFrecencyOrder()
    {
        using var store = new LibraryStore(null);
        var now = DateTimeOffset.UtcNow;
        store.RecordVisit("https://old.test/", "", at: now.AddDays(-100));
        store.RecordVisit("https://old.test/", "", at: now.AddDays(-100));
        store.RecordVisit("https://new.test/", "", at: now);
        store.RecordVisit("https://typed.test/", "", typed: true, at: now);
        Assert.Equal(["https://typed.test/", "https://new.test/", "https://old.test/"], store.TopHistory(3).Select(e => e.Url));
        store.RecomputeFrecency(now);
        Assert.NotNull(store.FrecencyComputed);
        Assert.Equal("https://typed.test/", store.TopHistory(1)[0].Url);
    }
}

public class SuggestionRankerTests
{
    private static SuggestionRanker.Candidate C(string url, string title, double frecency, SuggestionSource source, Guid? tab = null) =>
        new(url, title, frecency, source, TabId: tab);

    [Fact]
    public void HostPrefixBeatsHigherFrecencySubstring()
    {
        var ranked = SuggestionRanker.Rank("git", [
            C("https://example.com/digital", "Digital", 300, SuggestionSource.History),
            C("https://github.com/", "GitHub", 100, SuggestionSource.History),
        ], 5);
        Assert.Equal(["https://github.com/", "https://example.com/digital"], ranked.Select(r => r.Candidate.Url));
        Assert.True(ranked[0].MatchesHostPrefix);
    }

    [Fact]
    public void EveryWordMustMatch()
    {
        var ranked = SuggestionRanker.Rank("swift testing", [
            C("https://swift.org/", "Swift", 500, SuggestionSource.History),
            C("https://developer.apple.com/xcode/swift-testing/", "Swift Testing", 10, SuggestionSource.History),
        ], 5);
        Assert.Equal(["Swift Testing"], ranked.Select(r => r.Candidate.Title));
    }

    [Fact]
    public void BookmarksGetABoostAndDuplicatesMerge()
    {
        var ranked = SuggestionRanker.Rank("news", [
            C("https://news.a.test/", "A News", 100, SuggestionSource.History),
            C("https://news.b.test/", "B News", 30, SuggestionSource.History),
            C("https://news.b.test/", "B News", 0, SuggestionSource.Bookmark),
        ], 5);
        Assert.Equal(2, ranked.Count);
        Assert.Equal("https://news.b.test/", ranked[0].Candidate.Url);
        Assert.True(ranked[0].Candidate.IsBookmarked);
    }

    [Fact]
    public void OpenTabWinsTheMergedRow()
    {
        var id = Guid.NewGuid();
        var ranked = SuggestionRanker.Rank("exa", [
            C("https://example.com/", "Example", 400, SuggestionSource.History),
            C("https://example.com", "Example", 0, SuggestionSource.OpenTab, id),
        ], 5);
        Assert.Single(ranked);
        Assert.Equal(SuggestionSource.OpenTab, ranked[0].Candidate.Source);
        Assert.Equal(id, ranked[0].Candidate.TabId);
    }

    [Fact]
    public void EmptyInputGivesNothing() =>
        Assert.Empty(SuggestionRanker.Rank("   ", [C("https://a.test/", "A", 1, SuggestionSource.History)], 5));
}
