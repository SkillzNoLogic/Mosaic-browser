using System.Runtime.CompilerServices;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Productivity;

/// <summary>
/// Running scripts in a Chromium tab's page and hearing back from it: what the reader view
/// needs from WebView2 (<c>CoreWebView2.ExecuteScriptAsync</c> and <c>WebMessageReceived</c>).
/// </summary>
/// <remarks>
/// The winui area's Chromium browser can implement this directly; until it does, the app
/// supplies an adapter through <see cref="PageScripting.Resolver"/>. UI thread only.
/// </remarks>
public interface IPageScriptHost
{
    /// <summary>Whether a document exists that scripts can run in (the browser has been created).</summary>
    bool CanRunScripts { get; }

    /// <summary>
    /// Runs JavaScript in the page's main world. Returns the JSON-encoded value of the last
    /// expression (<c>"null"</c> for undefined), or null when it could not run.
    /// </summary>
    Task<string?> ExecuteScriptAsync(string script);

    /// <summary>Strings the page posted with <c>window.chrome.webview.postMessage</c>.</summary>
    event Action<string>? MessageReceived;
}

public static class PageScripting
{
    private static readonly ConditionalWeakTable<IChromiumBrowser, IPageScriptHost?> cache = [];

    /// <summary>Makes a script host for a browser that does not implement <see cref="IPageScriptHost"/> itself (set by the app).</summary>
    public static Func<IChromiumBrowser, IPageScriptHost?>? Resolver { get; set; }

    /// <summary>The script host of <paramref name="browser"/>, or null when it has none.</summary>
    public static IPageScriptHost? For(IChromiumBrowser? browser)
    {
        if (browser is null) return null;
        if (browser is IPageScriptHost host) return host;
        if (cache.TryGetValue(browser, out var cached) && cached is not null) return cached;
        var made = Resolver?.Invoke(browser);
        if (made is not null) cache.AddOrUpdate(browser, made);
        return made;
    }
}
