using Mosaic.Core.Productivity;

namespace Mosaic.Core.Tests.Productivity;

public class PaletteMatcherTests
{
    private static double? Score(string query, string title, params string[] keywords) => PaletteMatcher.Score(query, title, keywords);

    [Fact]
    public void GradesFromExactToFuzzy()
    {
        var exact = Score("new tab", "New Tab")!.Value;
        var prefix = Score("new", "New Tab")!.Value;
        var wordPrefix = Score("ta", "New Tab")!.Value;
        var initials = Score("nt", "New Tab")!.Value;
        var substring = Score("ew", "New Tab")!.Value;
        var fuzzy = Score("nwtb", "New Tab")!.Value;
        Assert.True(exact > prefix);
        Assert.True(prefix > wordPrefix);
        Assert.True(wordPrefix > initials);
        Assert.True(initials > substring);
        Assert.True(substring > fuzzy);
    }

    [Fact]
    public void NoMatchIsNull()
    {
        Assert.Null(Score("xyz", "New Tab"));
        Assert.Null(Score("tabs", "New Tab"));
    }

    [Fact]
    public void EveryWordMustMatchInAnyOrder()
    {
        Assert.NotNull(Score("close tab", "Close Tab"));
        Assert.Null(Score("close window", "Close Tab"));
        Assert.NotNull(Score("tab close", "Close Tab"));
    }

    [Fact]
    public void EmptyQueryMatchesWithZero()
    {
        Assert.Equal(0, Score("", "Anything"));
        Assert.Equal(0, Score("   ", "Anything"));
    }

    [Fact]
    public void CaseDiacriticsAndWidthAreIgnored()
    {
        Assert.NotNull(Score("ECOSIA", "Écosia"));
        Assert.NotNull(Score("cafe", "Café Menu"));
        Assert.NotNull(Score("tab", "ＴＡＢ"));
    }

    [Fact]
    public void KeywordsMatchButCountForLessThanTheTitle()
    {
        Assert.True(Score("settings", "Settings") > Score("settings", "Preferences", "settings"));
        Assert.NotNull(Score("prefs", "Settings", "preferences"));
    }

    [Fact]
    public void ShorterTitlesWinTiesAndWholePhraseWins()
    {
        Assert.True(Score("tab", "Tab") > Score("tab", "Tab Groups and Workspaces"));
        Assert.True(Score("show reader", "Show Reader View") > Score("show reader", "Reader: Show Larger Text"));
    }

    [Fact]
    public void WholeWordBeatsPrefixOfALongerWord()
    {
        var word = Score("tab", "New Tab")!.Value;
        var prefix = Score("tab", "Tabs & Workspaces Settings")!.Value;
        var leading = Score("tab", "Tab Groups")!.Value;
        Assert.True(leading > word);
        Assert.True(word > prefix);
    }

    private static readonly PaletteCandidate[] Candidates =
    [
        new("tab.new", "New Tab"),
        new("tab.close", "Close Tab"),
        new("reader", "Show Reader View", ["article"]),
        new("settings.tabs", "Tabs & Workspaces Settings", ["settings"]),
    ];

    [Fact]
    public void RanksBestFirstAndDropsNonMatches()
    {
        var ids = PaletteRanker.Rank(Candidates, "tab").Select(c => c.Id).ToList();
        Assert.DoesNotContain("reader", ids);
        Assert.Equal(["reader"], PaletteRanker.Rank(Candidates, "article").Select(c => c.Id));
        Assert.Equal(["tab.close"], PaletteRanker.Rank(Candidates, "clo").Select(c => c.Id));
        Assert.Equal("tab.new", PaletteRanker.Rank(Candidates, "new")[0].Id);
    }

    [Fact]
    public void RecentChoicesRankHigherAndBoostBreaksTies()
    {
        var plain = PaletteRanker.Rank(Candidates, "tab").Select(c => c.Id).ToHashSet();
        var recent = PaletteRanker.Rank(Candidates, "tab", ["tab.close"]).Select(c => c.Id).ToList();
        Assert.Equal("tab.close", recent[0]);
        Assert.Equal(plain, recent.ToHashSet());
        PaletteCandidate[] items = [new("a", "Example"), new("b", "Example", [], 5)];
        Assert.Equal(["b", "a"], PaletteRanker.Rank(items, "exa").Select(c => c.Id));
    }

    [Fact]
    public void EmptyQueryListsEverythingRecentFirstThenInOrder()
    {
        var ids = PaletteRanker.Rank(Candidates, "", ["reader"]).Select(c => c.Id).ToList();
        Assert.Equal(["reader", "tab.new", "tab.close", "settings.tabs"], ids);
        Assert.Equal(2, PaletteRanker.Rank(Candidates, "", limit: 2).Count);
    }

    [Fact]
    public void RecentsDeduplicateAndCap()
    {
        var recents = new PaletteRecents(capacity: 3);
        foreach (var id in new[] { "a", "b", "c", "a", "d" }) recents.Note(id);
        Assert.Equal(["d", "a", "c"], recents.Ids);
    }
}
