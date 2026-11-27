using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;

namespace Mosaic.App.Chromium;

/// <summary>
/// Mosaic's Chromium engine: the Microsoft Edge WebView2 Runtime, with one
/// <see cref="WebView2Browser"/> per Chromium tab and one environment (and so one profile, in
/// <see cref="AppPaths.ChromiumData"/>) shared by all of them.
/// </summary>
/// <remarks>
/// <para>The runtime is a separate install that is present on Windows 11 and most Windows 10
/// machines but not guaranteed. If it is missing the engine reports itself unavailable and Mosaic
/// runs on Gecko alone.</para>
/// <para>The environment is created with browser extensions enabled, which is what lets
/// <see cref="IChromiumExtensionHost"/> work. That part is experimental; see
/// <see cref="ExtensionManager"/>.</para>
/// </remarks>
internal sealed class WebView2Engine : IChromiumEngine, IChromiumExtensionHost
{
    private const string RuntimeDownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>The first page of a session waits at most this long for extensions to load.</summary>
    private static readonly TimeSpan ExtensionSyncPatience = TimeSpan.FromSeconds(5);

    private readonly List<WebView2Browser> readyBrowsers = [];
    private Task<CoreWebView2Environment>? environment;
    private Task? extensionSync;

    public WebView2Engine()
    {
        try
        {
            RuntimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            IsAvailable = !string.IsNullOrEmpty(RuntimeVersion);
        }
        catch (Exception)
        {
            // WebView2RuntimeNotFoundException when the runtime is not installed; a loader that
            // cannot be found or loaded surfaces as other exception types. Whatever the cause,
            // the outcome is the same: no Chromium engine, and Mosaic carries on with Gecko.
            IsAvailable = false;
        }

        if (!IsAvailable)
        {
            UnavailableReason =
                "The Chromium engine needs the Microsoft Edge WebView2 Runtime, which was not found on this PC. " +
                $"Install it from {RuntimeDownloadPage} and restart Mosaic.";
        }
    }

    public bool IsAvailable { get; }

    public string? UnavailableReason { get; }

    /// <summary>Version of the installed runtime, e.g. "140.0.3485.54"; null if there is none.</summary>
    public string? RuntimeVersion { get; }

    /// <summary>
    /// Where browser views live: a panel in the window's content area. Every Chromium tab's view
    /// stays in it, hidden unless its tab is showing, because a WebView2 only runs while it is
    /// part of a window.
    /// </summary>
    public Panel? ViewHost { get; set; }

    /// <summary>
    /// The content blocker's decision point, consulted for every request of every Chromium tab;
    /// null when the blocker is not part of this build, in which case requests are not intercepted
    /// at all. Set before the first tab is created: a browser picks it up when it starts.
    /// </summary>
    public RequestFilter? RequestFilter { get; set; }

    public IChromiumBrowser CreateBrowser(string url)
    {
        var browser = new WebView2Browser(this, url);
        ViewHost?.Children.Add(browser.View);
        browser.Start();
        return browser;
    }

    /// <summary>The shared environment, created on first use and re-created if that failed.</summary>
    internal Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (environment is null or { IsFaulted: true } or { IsCanceled: true })
        {
            var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
            environment = CoreWebView2Environment.CreateAsync(null, AppPaths.ChromiumData, options);
        }
        return environment;
    }

    /// <summary>
    /// Called by a browser once its WebView2 exists and before it navigates. The first browser of
    /// a session brings the profile into being, which is the moment to load extensions into it.
    /// </summary>
    internal async Task BrowserReadyAsync(WebView2Browser browser)
    {
        readyBrowsers.Add(browser);
        if (readyBrowsers.Count == 1) extensionSync = OnReady?.Invoke();
        if (extensionSync is { } sync)
        {
            // Extensions are a nicety; a slow profile must not hold the page up for long.
            await Task.WhenAny(sync, Task.Delay(ExtensionSyncPatience));
        }
    }

    internal void BrowserClosed(WebView2Browser browser)
    {
        ViewHost?.Children.Remove(browser.View);
        readyBrowsers.Remove(browser);
        if (readyBrowsers.Count == 0) extensionSync = null;
    }

    /// <summary>User-presentable text for a WebView2 failure.</summary>
    internal static string Describe(Exception error) =>
        error is WebView2RuntimeNotFoundException
            ? $"The Microsoft Edge WebView2 Runtime is no longer installed. Install it from {RuntimeDownloadPage}."
            : $"The Chromium engine could not start: {error.Message}";

    // ---- IChromiumExtensionHost ----

    public bool IsReady => readyBrowsers.Count > 0;

    public Func<Task>? OnReady { get; set; }

    public async Task<IReadOnlyCollection<string>> ListAsync()
    {
        var extensions = await Profile.GetBrowserExtensionsAsync();
        return extensions.Select(extension => extension.Id).ToList();
    }

    public async Task<string> LoadAsync(string folder)
    {
        var extension = await Profile.AddBrowserExtensionAsync(folder);
        return extension.Id;
    }

    public async Task UnloadAsync(string chromiumId)
    {
        var extensions = await Profile.GetBrowserExtensionsAsync();
        if (extensions.FirstOrDefault(extension => extension.Id == chromiumId) is { } extension)
        {
            await extension.RemoveAsync();
        }
    }

    /// <summary>
    /// The profile, reached through whichever browser is alive: WebView2 only hands it out via a
    /// live <see cref="CoreWebView2"/>.
    /// </summary>
    private CoreWebView2Profile Profile =>
        readyBrowsers.FirstOrDefault()?.View.CoreWebView2?.Profile
        ?? throw new InvalidOperationException("No Chromium tab is open, so the Chromium profile is not available.");
}
