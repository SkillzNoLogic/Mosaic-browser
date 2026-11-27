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
    private BlockerSettings settings = new();

    /// <summary>Puts <paramref name="replacement"/> in charge and returns the engine it replaces,
    /// which the caller owns and should dispose.</summary>
    public BlockerEngine? Use(BlockerEngine? replacement)
    {
        lock (gate)
        {
            var previous = engine;
            engine = replacement;
            return previous;
        }
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
        return current is not null && choices.Filters(documentUrl)
            && current.Verdict(url, documentUrl, type) == BlockerVerdict.Block;
    }
}
