namespace Mosaic.Core.Search;

/// <summary>A web search provider the address bar can send queries to.</summary>
/// <remarks>Port of <c>Sources/MosaicCore/Search/SearchEngine.swift</c>.</remarks>
/// <param name="Id">Stable identifier used in settings and automation (<c>"duckduckgo"</c>).</param>
/// <param name="Name">Name shown in the search engine picker.</param>
/// <param name="SearchTemplate">Search URL with <c>%s</c> standing in for the percent-encoded query.</param>
/// <param name="Homepage">The provider's start page.</param>
public sealed record SearchEngine(string Id, string Name, string SearchTemplate, string Homepage)
{
    public static readonly SearchEngine Google = new("google", "Google", "https://www.google.com/search?q=%s", "https://www.google.com/");
    public static readonly SearchEngine DuckDuckGo = new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q=%s", "https://duckduckgo.com/");
    public static readonly SearchEngine Bing = new("bing", "Bing", "https://www.bing.com/search?q=%s", "https://www.bing.com/");
    public static readonly SearchEngine Brave = new("brave", "Brave Search", "https://search.brave.com/search?q=%s", "https://search.brave.com/");
    public static readonly SearchEngine Startpage = new("startpage", "Startpage", "https://www.startpage.com/sp/search?query=%s", "https://www.startpage.com/");
    public static readonly SearchEngine Ecosia = new("ecosia", "Ecosia", "https://www.ecosia.org/search?q=%s", "https://www.ecosia.org/");
    public static readonly SearchEngine Kagi = new("kagi", "Kagi", "https://kagi.com/search?q=%s", "https://kagi.com/");

    /// <summary>The engines offered in the picker, in display order.</summary>
    public static IReadOnlyList<SearchEngine> BuiltIn { get; } =
        [Google, DuckDuckGo, Bing, Brave, Startpage, Ecosia, Kagi];

    /// <summary>The engine used until the user picks one.</summary>
    public static SearchEngine Default => Google;

    /// <summary>The engine with this <see cref="Id"/> (built in, or one of <see cref="Registered"/>), or null.</summary>
    public static SearchEngine? Named(string id) =>
        BuiltIn.FirstOrDefault(engine => engine.Id == id) ?? Registered.FirstOrDefault(engine => engine.Id == id);

    private static volatile IReadOnlyList<SearchEngine> registered = [];

    /// <summary>
    /// Engines added at run time (the user's custom search engines, kept up to date by the
    /// library), so <see cref="Named"/> finds them and one can be the default.
    /// </summary>
    public static IReadOnlyList<SearchEngine> Registered
    {
        get => registered;
        set => registered = [.. value];
    }

    /// <summary>Built-in engines followed by <see cref="Registered"/> ones (for pickers).</summary>
    public static IReadOnlyList<SearchEngine> All => [.. BuiltIn, .. Registered];

    /// <summary>The engine after this one in <see cref="BuiltIn"/>, wrapping around.</summary>
    /// <remarks>An engine that is not built in is followed by <see cref="Default"/>.</remarks>
    public SearchEngine Next
    {
        get
        {
            for (var index = 0; index < BuiltIn.Count; index++)
            {
                if (BuiltIn[index] == this) return BuiltIn[(index + 1) % BuiltIn.Count];
            }
            return Default;
        }
    }

    /// <summary>
    /// The search URL for <paramref name="query"/>: the template with <c>%s</c> replaced by the
    /// query percent-encoded so that only unreserved characters (A–Z a–z 0–9 - . _ ~) stay literal.
    /// </summary>
    /// <returns>The URL, or null when the template does not produce one (it is empty or malformed).</returns>
    public string? SearchUrl(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var url = SearchTemplate.Replace("%s", FoundationUrl.EscapeQueryValue(query), StringComparison.Ordinal);
        return FoundationUrl.Parse(url)?.AbsoluteString;
    }
}
