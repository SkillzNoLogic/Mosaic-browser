using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Library;

namespace Mosaic.Core.Search;

/// <summary>
/// Address-bar keywords ("w swift" searches Wikipedia), suggestion endpoints and the parsing
/// of their answers.
/// </summary>
/// <remarks>Port of <c>Sources/MosaicCore/Library/SearchShortcuts.swift</c>.</remarks>
public static class SearchShortcuts
{
    /// <summary>What a keyword turned the input into.</summary>
    public sealed record Match(SearchEngine Engine, string Keyword, string Terms);

    private static readonly System.Buffers.SearchValues<char> Whitespace = System.Buffers.SearchValues.Create(
        [.. Enumerable.Range(0, char.MaxValue + 1).Select(code => (char)code).Where(char.IsWhiteSpace)]);

    /// <summary>The fixed keyword of each built-in engine ("@google", "@ddg"…).</summary>
    public static string? Keyword(SearchEngine engine) => engine.Id switch
    {
        "google" => "@google",
        "duckduckgo" => "@ddg",
        "bing" => "@bing",
        "brave" => "@brave",
        "startpage" => "@startpage",
        "ecosia" => "@ecosia",
        "kagi" => "@kagi",
        _ => null,
    };

    /// <summary>
    /// "keyword terms…" → the engine and the terms, or null when the first word is no keyword
    /// or nothing follows it.
    /// </summary>
    public static Match? MatchKeyword(string input, IEnumerable<CustomSearchEngine> custom)
    {
        var text = input.Trim();
        var space = text.AsSpan().IndexOfAny(Whitespace);
        if (space < 0) return null;
        var word = text[..space].ToLowerInvariant();
        var terms = text[space..].Trim();
        if (word.Length == 0 || terms.Length == 0) return null;
        if (custom.FirstOrDefault(engine => engine.Keyword.Length > 0 && engine.Keyword.ToLowerInvariant() == word) is { } found)
        {
            return new Match(found.SearchEngine, found.Keyword, terms);
        }
        if (SearchEngine.BuiltIn.FirstOrDefault(engine => Keyword(engine) == word) is { } builtIn)
        {
            return new Match(builtIn, word, terms);
        }
        return null;
    }

    /// <summary>Like <see cref="AddressResolver.Resolve"/>, but a keyword search wins first.</summary>
    public static string? Resolve(string input, SearchEngine searchEngine, IEnumerable<CustomSearchEngine> custom) =>
        MatchKeyword(input, custom) is { } match ? match.Engine.SearchUrl(match.Terms) : AddressResolver.Resolve(input, searchEngine);

    /// <summary>The OpenSearch suggestion endpoint (<c>%s</c> for the terms) of an engine, if it has one.</summary>
    public static string? SuggestTemplate(SearchEngine engine, IEnumerable<CustomSearchEngine> custom)
    {
        if (custom.FirstOrDefault(entry => entry.Id == engine.Id) is { } entry) return entry.SuggestTemplate;
        return engine.Id switch
        {
            "google" => "https://suggestqueries.google.com/complete/search?client=firefox&q=%s",
            "duckduckgo" => "https://duckduckgo.com/ac/?q=%s&type=list",
            "bing" => "https://www.bing.com/osjson.aspx?query=%s&form=OSDJAS",
            "brave" => "https://search.brave.com/api/suggest?q=%s",
            "startpage" => "https://www.startpage.com/osuggestions?q=%s",
            "ecosia" => "https://ac.ecosia.org/autocomplete?q=%s&type=list",
            "kagi" => "https://kagi.com/api/autosuggest?q=%s",
            _ => null,
        };
    }

    public static string? SuggestUrl(string template, string query)
    {
        var url = template.Replace("%s", FoundationUrl.EscapeQueryValue(query), StringComparison.Ordinal);
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" ? url : null;
    }

    /// <summary>OpenSearch suggestions: <c>["query", ["one", "two", …], …]</c>.</summary>
    public static IReadOnlyList<string> ParseSuggestions(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonArray { Count: > 1 } array && array[1] is JsonArray list)
            {
                return [.. list.OfType<JsonValue>().Select(value => value.TryGetValue(out string? text) ? text : null).OfType<string>()];
            }
        }
        catch (JsonException)
        {
        }
        return [];
    }
}
