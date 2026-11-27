using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Search;

/// <summary>Port of <c>SearchEngineTests</c> in <c>Tests/MosaicCoreTests/SearchTests.swift</c>, plus edge cases.</summary>
public class SearchEngineTests
{
    [Fact]
    public void BuildsSearchUrlWithEncodedQuery() =>
        Assert.Equal(
            "https://www.google.com/search?q=swift%20ui%20%26%20c%2B%2B%20%231",
            SearchEngine.Google.SearchUrl("swift ui & c++ #1"));

    [Fact]
    public void EveryBuiltInEngineProducesAUrl()
    {
        foreach (var engine in SearchEngine.BuiltIn)
        {
            var url = engine.SearchUrl("mosaic");
            Assert.NotNull(url);
            Assert.StartsWith("https://", url);
            Assert.Contains("mosaic", url);
        }
    }

    [Fact]
    public void BuiltInIdsAreUnique() =>
        Assert.Equal(SearchEngine.BuiltIn.Count, SearchEngine.BuiltIn.Select(engine => engine.Id).ToHashSet().Count);

    [Fact]
    public void NextCyclesThroughAllAndWraps()
    {
        var engine = SearchEngine.BuiltIn[0];
        var seen = new List<SearchEngine>();
        foreach (var _ in SearchEngine.BuiltIn)
        {
            seen.Add(engine);
            engine = engine.Next;
        }
        Assert.Equal(SearchEngine.BuiltIn, seen);
        Assert.Equal(SearchEngine.BuiltIn[0], engine);
    }

    [Fact]
    public void LookupById()
    {
        Assert.Equal(SearchEngine.DuckDuckGo, SearchEngine.Named("duckduckgo"));
        Assert.Null(SearchEngine.Named("altavista"));
    }

    [Fact]
    public void AnEngineThatIsNotBuiltInIsFollowedByTheDefault()
    {
        var custom = new SearchEngine("custom", "Custom", "https://search.example/?q=%s", "https://search.example/");
        Assert.Equal(SearchEngine.Default, custom.Next);
    }

    [Fact]
    public void AnEqualCopyOfABuiltInEngineCyclesLikeTheOriginal()
    {
        var copy = SearchEngine.Kagi with { };
        Assert.Equal(SearchEngine.Google, copy.Next);
    }

    [Theory]
    [InlineData("Zürich café", "Z%C3%BCrich%20caf%C3%A9")]
    [InlineData("日本語", "%E6%97%A5%E6%9C%AC%E8%AA%9E")]
    [InlineData("😀", "%F0%9F%98%80")]
    [InlineData("a-b.c_d~e", "a-b.c_d~e")]
    [InlineData("100%", "100%25")]
    [InlineData("a/b?c=d&e", "a%2Fb%3Fc%3Dd%26e")]
    [InlineData("line\nbreak\ttab", "line%0Abreak%09tab")]
    [InlineData("", "")]
    public void OnlyUnreservedCharactersStayLiteral(string query, string encoded) =>
        Assert.Equal($"https://duckduckgo.com/?q={encoded}", SearchEngine.DuckDuckGo.SearchUrl(query));

    [Fact]
    public void AnUnpairedSurrogateIsEncodedAsTheReplacementCharacter() =>
        Assert.Equal("https://duckduckgo.com/?q=a%EF%BF%BDb", SearchEngine.DuckDuckGo.SearchUrl("a\uD83Db"));

    [Fact]
    public void EveryPlaceholderInATemplateIsReplaced()
    {
        var engine = new SearchEngine("x", "X", "https://x.example/%s?again=%s", "https://x.example/");
        Assert.Equal("https://x.example/a%20b?again=a%20b", engine.SearchUrl("a b"));
    }

    [Fact]
    public void ATemplateThatIsNotAUrlProducesNothing()
    {
        Assert.Null(new SearchEngine("x", "X", "", "").SearchUrl("query"));
        Assert.Null(new SearchEngine("x", "X", "https://x.example:port/?q=%s", "").SearchUrl("query"));
    }
}
