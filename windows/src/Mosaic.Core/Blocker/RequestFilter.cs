namespace Mosaic.Core.Blocker;

/// <summary>
/// The decision point for Chromium requests: the current engine and the user's settings,
/// readable from any thread.
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Blocker/RequestFilter.swift</c>. WebView2 asks on the UI thread
/// for every request, while a freshly built engine is handed over from a background build and the
/// user changes settings; this type is the one place those meet. The engine itself serialises
/// calls into its isolate, so the lock here only guards the two references.
/// </remarks>
public sealed class RequestFilter
{
    private readonly Lock gate = new();
    private BlockerEngine? engine;
    private CosmeticStyleCache? cosmetics;
    private BlockerSettings settings = new();
    private volatile bool cosmeticsEnabled = true;

    /// <summary>Puts <paramref name="replacement"/> in charge and returns the engine it replaces,
    /// which the caller owns and should dispose.</summary>
    public BlockerEngine? Use(BlockerEngine? replacement)
    {
        lock (gate)
        {
            var previous = engine;
            engine = replacement;
            cosmetics = replacement is null ? null : new CosmeticStyleCache(replacement);
            return previous;
        }
    }

    /// <summary>The engine in charge, if one has loaded; also answers the dashboard and filter validation.</summary>
    public BlockerEngine? Engine
    {
        get { lock (gate) return engine; }
    }

    /// <summary>The settings in effect.</summary>
    public BlockerSettings Settings
    {
        get { lock (gate) return settings; }
    }

    /// <summary>Requests blocked in Chromium tabs, per page, for the dashboard. <see cref="ShouldBlock"/> records into it.</summary>
    public BlockLog Log { get; } = new();

    /// <summary>Hide page elements named by cosmetic filters (<c>##.ad</c>) in Chromium tabs.</summary>
    public bool CosmeticsEnabled { get => cosmeticsEnabled; set => cosmeticsEnabled = value; }

    /// <summary>
    /// The style-injection script for a Chromium frame, or null. The generic stylesheet (half a
    /// megabyte with the default lists) goes to the page itself and to frames on the page's own
    /// host; other frames get only their site's specific rules, which keeps ad-heavy pages with
    /// many third-party frames fast. A main frame committing a new document also starts its
    /// dashboard log over. Port of <c>ContentBlocker.cosmeticScript</c> on macOS.
    /// </summary>
    /// <param name="frameUrl">The frame's document.</param>
    /// <param name="pageUrl">The tab's page (the top-level document).</param>
    /// <param name="isMainFrame">The frame is the page itself.</param>
    /// <param name="newDocument">False when asking again for a document already asked about (the log is kept).</param>
    public string? CosmeticScript(string frameUrl, string pageUrl, bool isMainFrame, bool newDocument = true)
    {
        if (isMainFrame && newDocument) Log.Clear(frameUrl);
        CosmeticStyleCache? cache;
        BlockerSettings choices;
        lock (gate)
        {
            cache = cosmetics;
            choices = settings;
        }
        if (!cosmeticsEnabled || cache is null || !choices.Filters(pageUrl)) return null;
        if (!Uri.TryCreate(frameUrl, UriKind.Absolute, out var frame) || frame.Host.Length == 0) return null;
        var pageHost = Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) ? page.Host : null;
        var css = cache.StylesheetFor(frame.Host, includeGeneric: isMainFrame || string.Equals(pageHost, frame.Host, StringComparison.OrdinalIgnoreCase));
        return CosmeticInjection.Script(css);
    }

    public void Use(BlockerSettings replacement)
    {
        lock (gate) settings = replacement;
    }

    /// <summary>
    /// Whether a request made by the page at <paramref name="documentUrl"/> is to be blocked. False
    /// while no engine is loaded, when blocking is off, for pages that are not on the web, and for
    /// sites the user has exempted.
    /// </summary>
    public bool ShouldBlock(string url, string documentUrl, string type)
    {
        BlockerEngine? current;
        BlockerSettings choices;
        lock (gate)
        {
            current = engine;
            choices = settings;
        }
        var blocked = current is not null && choices.Filters(documentUrl)
            && current.Verdict(url, documentUrl, type) == BlockerVerdict.Block;
        if (blocked) Log.Record(url, documentUrl, type);
        return blocked;
    }
}
