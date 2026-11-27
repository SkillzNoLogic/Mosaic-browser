namespace Mosaic.Core.Search;

/// <summary>A web search provider the address bar can send queries to.</summary>
/// <remarks>Port of <c>Sources/MosaicCore/Search/SearchEngine.swift</c>.</remarks>
public sealed record SearchEngine(string Id, string Name, string SearchTemplate, string Homepage)
{
    public static readonly SearchEngine Google = new("google", "Google", "https://www.google.com/search?q=%s", "https://www.google.com/");
    public static readonly SearchEngine DuckDuckGo = new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q=%s", "https://duckduckgo.com/");
    public static readonly SearchEngine Bing = new("bing", "Bing", "https://www.bing.com/search?q=%s", "https://www.bing.com/");
    public static readonly SearchEngine Brave = new("brave", "Brave Search", "https://search.brave.com/search?q=%s", "https://search.brave.com/");
    public static readonly SearchEngine Startpage = new("startpage", "Startpage", "https://www.startpage.com/sp/search?query=%s", "https://www.startpage.com/");
    public static readonly SearchEngine Ecosia = new("ecosia", "Ecosia", "https://www.ecosia.org/search?q=%s", "https://www.ecosia.org/");
    public static readonly SearchEngine Kagi = new("kagi", "Kagi", "https://kagi.com/search?q=%s", "https://kagi.com/");

    public static IReadOnlyList<SearchEngine> BuiltIn { get; } =
        [Google, DuckDuckGo, Bing, Brave, Startpage, Ecosia, Kagi];

    public static SearchEngine Default => Google;

    public static SearchEngine? Named(string id) => BuiltIn.FirstOrDefault(engine => engine.Id == id);

    /// <summary>The engine after this one in <see cref="BuiltIn"/>, wrapping around.</summary>
    public SearchEngine Next => throw NotPorted.Yet();

    /// <summary>
    /// The search URL for <paramref name="query"/>: the template with <c>%s</c> replaced by the
    /// query percent-encoded so that only unreserved characters (A–Z a–z 0–9 - . _ ~) stay literal.
    /// </summary>
    public string? SearchUrl(string query) => throw NotPorted.Yet();
}
