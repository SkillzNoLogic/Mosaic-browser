using Mosaic.Core.Library;
using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Search;

/// <summary>Port of <c>SearchShortcutTests</c> in <c>Tests/MosaicCoreTests/LibraryTests.swift</c>.</summary>
public class SearchShortcutsTests
{
    private static readonly CustomSearchEngine[] Custom =
    [
        new() { Id = "custom.wiki", Name = "Wikipedia", Keyword = "w", Template = "https://en.wikipedia.org/wiki/Special:Search?search=%s" },
    ];

    [Fact]
    public void KeywordSearches()
    {
        var match = SearchShortcuts.MatchKeyword("w  swift language ", Custom)!;
        Assert.Equal("Wikipedia", match.Engine.Name);
        Assert.Equal("swift language", match.Terms);
        Assert.Equal("https://en.wikipedia.org/wiki/Special:Search?search=swift%20language",
            SearchShortcuts.Resolve("W swift language", SearchEngine.Google, Custom));
    }

    [Fact]
    public void BuiltInAtKeywords()
    {
        Assert.StartsWith("https://duckduckgo.com/", SearchShortcuts.Resolve("@ddg privacy", SearchEngine.Google, Custom));
        foreach (var engine in SearchEngine.BuiltIn) Assert.NotNull(SearchShortcuts.Keyword(engine));
    }

    [Fact]
    public void NotAKeyword()
    {
        Assert.Null(SearchShortcuts.MatchKeyword("w", Custom));
        Assert.Null(SearchShortcuts.MatchKeyword("w   ", Custom));
        Assert.Null(SearchShortcuts.MatchKeyword("wikipedia swift", Custom));
        Assert.StartsWith("https://w3schools.com", SearchShortcuts.Resolve("w3schools.com", SearchEngine.Google, Custom));
        Assert.StartsWith("https://www.google.com/", SearchShortcuts.Resolve("what is swift", SearchEngine.Google, Custom));
    }

    [Fact]
    public void CustomEnginesCanBeLookedUpOnceRegistered()
    {
        var engine = Custom[0].SearchEngine;
        Assert.Equal("https://en.wikipedia.org/", engine.Homepage);
        // Every library registers the seeded Wikipedia engine, so look up that one.
        var seeded = new CustomSearchEngine { Id = "custom.wikipedia", Name = "Wikipedia", Keyword = "w", Template = engine.SearchTemplate };
        SearchEngine.Registered = [seeded.SearchEngine];
        Assert.Equal("custom.wikipedia", SearchEngine.Named("custom.wikipedia")?.Id);
        Assert.Contains(SearchEngine.All, e => e.Id == "custom.wikipedia");
    }

    [Fact]
    public void Validation()
    {
        Assert.Null(CustomSearchEngine.Problem("X", "x", "https://x.test/?q=%s"));
        Assert.NotNull(CustomSearchEngine.Problem("", "x", "https://x.test/?q=%s"));
        Assert.NotNull(CustomSearchEngine.Problem("X", "a b", "https://x.test/?q=%s"));
        Assert.NotNull(CustomSearchEngine.Problem("X", "x", "https://x.test/"));
        Assert.NotNull(CustomSearchEngine.Problem("X", "x", "javascript:%s"));
    }

    [Fact]
    public void SuggestionParsingAndUrls()
    {
        Assert.Equal(["swift code", "swift ui"], SearchShortcuts.ParseSuggestions("""["swift",["swift code","swift ui"],[],{"x":1}]"""));
        Assert.Empty(SearchShortcuts.ParseSuggestions("{}"));
        Assert.Empty(SearchShortcuts.ParseSuggestions("not json"));
        foreach (var engine in SearchEngine.BuiltIn)
        {
            var template = SearchShortcuts.SuggestTemplate(engine, []);
            Assert.NotNull(template);
            Assert.StartsWith("https://", SearchShortcuts.SuggestUrl(template!, "a b&c"));
        }
        Assert.Equal("https://s.test/?q=a%20b%26c", SearchShortcuts.SuggestUrl("https://s.test/?q=%s", "a b&c"));
    }

    [Fact]
    public void MosaicPagesAreAddresses()
    {
        Assert.Equal("mosaic://history", AddressResolver.Resolve("mosaic://history", SearchEngine.Google));
        Assert.True(AddressResolver.IsAddress("mosaic://bookmarks"));
    }
}
