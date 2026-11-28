using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;

namespace Mosaic.App.Chromium;

/// <summary>One Chromium tab: a WebView2 control and the translation of its events for the model.</summary>
/// <remarks>
/// <para>
/// Creating the underlying browser is asynchronous (it may have to start the WebView2 processes),
/// so a browser begins as an empty, hidden control that remembers the address it was asked to
/// load. Everything is reported through events, and only after <see cref="Start"/> has returned,
/// so the model has always subscribed by the time anything is raised.
/// </para>
/// <para>
/// <b>Content blocking.</b> The counterpart of <c>OnBeforeResourceLoad</c> in macOS's
/// <c>CEFBridge.mm</c>: every request the page makes (including its iframes and dedicated workers)
/// is put to the engine's <see cref="RequestFilter"/> through <c>WebResourceRequested</c>, and a
/// blocked one is answered with an empty error response instead of going to the network. The
/// page's own document is never blocked. WebView2 raises the event on the UI thread and holds the
/// request until the handler returns, so the handler does nothing slow: two property reads, the
/// filter's settings check and, for web pages, one call into the engine.
/// </para>
/// </remarks>
internal sealed class WebView2Browser : IChromiumBrowser
{
    /// <summary>Status of the response a blocked request gets in place of the real one.</summary>
    private const int BlockedStatus = 403;

    private readonly WebView2Engine engine;
    private string pendingUrl;
    private bool isReady;
    private bool isClosed;
    private bool isLoading = true;

    /// <summary>The committed address of the page, which filter rules mean by "the page" (first party).</summary>
    private string documentUrl = "";
    /// <summary>Where the latest top-level navigation is going, as <see cref="DocumentKey"/> spells it.</summary>
    private string? navigationKey;
    private int blockedCount;

    public WebView2Browser(WebView2Engine engine, string url)
    {
        this.engine = engine;
        pendingUrl = url;
    }

    /// <summary>The control to place in the window. Hidden until <see cref="IsShown"/> is set.</summary>
    public WebView2 View { get; } = new() { Visibility = Visibility.Collapsed };

    public bool IsShown
    {
        get => View.Visibility == Visibility.Visible;
        // Collapsed rather than covered: WebView2 stops rendering while it is not visible, and
        // only the selected tab's page should take memory and GPU time.
        set => View.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public event Action<string>? TitleChanged;
    public event Action<string>? UrlChanged;
    public event Action<ChromiumLoadState>? LoadStateChanged;
    public event Action<string>? NewTabRequested;
    public event Action<int>? BlockedCountChanged;
    public event Action<string>? Failed;

    /// <summary>Begins creating the browser. Call once, after <see cref="View"/> is in the window.</summary>
    public void Start() => _ = InitializeAsync();

    private async Task InitializeAsync()
    {
        // Never finish (or fail) synchronously: see the class remarks.
        await Task.Yield();
        try
        {
            var environment = await engine.GetEnvironmentAsync();
            if (isClosed) return;
            await View.EnsureCoreWebView2Async(environment);
            if (isClosed) return;

            var core = View.CoreWebView2;
            core.DocumentTitleChanged += (_, _) => Raise(() => TitleChanged?.Invoke(core.DocumentTitle));
            core.SourceChanged += (_, _) =>
            {
                documentUrl = core.Source;
                Raise(() => UrlChanged?.Invoke(documentUrl));
            };
            core.NavigationStarting += (_, navigation) =>
            {
                // Raised for top-level navigations only (frames have their own event), again for
                // each redirect, and not for fragment changes within the page.
                navigationKey = DocumentKey(navigation.Uri);
                StartBlockedCountOver();
                ReportLoadState(core, loading: true);
            };
            core.NavigationCompleted += (_, _) => ReportLoadState(core, loading: false);
            core.HistoryChanged += (_, _) => ReportLoadState(core, isLoading);
            core.NewWindowRequested += (_, request) =>
            {
                // Mosaic has one window: anything that asks for another gets a tab.
                request.Handled = true;
                Raise(() => NewTabRequested?.Invoke(request.Uri));
            };
            core.ProcessFailed += (_, failure) =>
            {
                // Chromium recovers from, or shows its own page for, a renderer that died. Only
                // the loss of the browser process leaves this control unusable.
                if (failure.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    Raise(() => Failed?.Invoke("Chromium stopped unexpectedly. Reload to start it again."));
                }
            };

            // Filters only apply to navigations that start after they are added.
            if (engine.RequestFilter is not null) WatchRequests(core, environment);

            // Extensions have to be in the profile before the page loads to apply to it.
            await engine.BrowserReadyAsync(this);
            if (isClosed) return;

            isReady = true;
            Navigate(pendingUrl);
        }
        catch (Exception error)
        {
            // WebView2 reports failures to start as COM errors of many kinds; all of them mean
            // this tab has no browser, and the tab says so.
            Raise(() => Failed?.Invoke(WebView2Engine.Describe(error)));
        }
    }

    public void Load(string url)
    {
        if (isReady) Navigate(url);
        else pendingUrl = url;
    }

    public void GoBack() => WithCore(core =>
    {
        if (core.CanGoBack) core.GoBack();
    });

    public void GoForward() => WithCore(core =>
    {
        if (core.CanGoForward) core.GoForward();
    });

    public void Reload() => WithCore(core => core.Reload());

    public void Stop() => WithCore(core => core.Stop());

    public void Focus()
    {
        if (!isClosed && View.Visibility == Visibility.Visible) View.Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        engine.BrowserClosed(this);
        try
        {
            View.Close();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            // The browser process is already gone; there is nothing left to release.
        }
    }

    private void Navigate(string url) => WithCore(core =>
    {
        try
        {
            core.Navigate(url);
        }
        catch (ArgumentException)
        {
            // Not something Chromium accepts as an address. Nothing is loading after all.
            ReportLoadState(core, loading: false);
        }
    });

    // ---- Content blocking ----

    private void WatchRequests(CoreWebView2 core, CoreWebView2Environment environment)
    {
        try
        {
            // Requests from the page and everything that runs on its behalf: iframes, dedicated
            // workers. Shared and service workers belong to no one tab, and Gecko lets their
            // traffic through as well.
            core.AddWebResourceRequestedFilter(
                "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
        }
        catch (NotImplementedException)
        {
            // A runtime older than 1.0.2365.46 lacks the overload above. The original one covers
            // the page itself but, per its documentation, not reliably its iframes.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        }
        core.WebResourceRequested += (_, request) => OnWebResourceRequested(request, environment);
    }

    private void OnWebResourceRequested(CoreWebView2WebResourceRequestedEventArgs request, CoreWebView2Environment environment)
    {
        if (isClosed || engine.RequestFilter is not { } filter) return;
        try
        {
            var context = request.ResourceContext;
            var url = request.Request.Uri;
            // The page's own document is never blocked. Its request is the one the latest
            // top-level navigation announced; any other document is a frame inside the page.
            if (context == CoreWebView2WebResourceContext.Document && DocumentKey(url) == navigationKey) return;
            if (!filter.ShouldBlock(url, documentUrl, WebRequestType(context))) return;

            request.Response = environment.CreateWebResourceResponse(null, BlockedStatus, "Blocked by Mosaic", "");
        }
        catch (COMException)
        {
            // The browser went away mid-request; there is nothing left to block.
            return;
        }
        blockedCount++;
        Raise(() => BlockedCountChanged?.Invoke(blockedCount));
    }

    private void StartBlockedCountOver()
    {
        if (blockedCount == 0) return;
        blockedCount = 0;
        Raise(() => BlockedCountChanged?.Invoke(0));
    }

    /// <summary>
    /// WebView2's resource contexts under the names the webRequest API, and so uBlock Origin's
    /// filter syntax, uses for them; the counterpart of <c>WebRequestType</c> in <c>CEFBridge.mm</c>.
    /// </summary>
    private static string WebRequestType(CoreWebView2WebResourceContext context) => context switch
    {
        // The top-level document is filtered out before this; what is left are frames.
        CoreWebView2WebResourceContext.Document => "sub_frame",
        CoreWebView2WebResourceContext.Stylesheet => "stylesheet",
        CoreWebView2WebResourceContext.Script => "script",
        CoreWebView2WebResourceContext.Image => "image",
        CoreWebView2WebResourceContext.Font => "font",
        CoreWebView2WebResourceContext.Media => "media",
        // WebView2 documents fetch() and EventSource as arriving under XmlHttpRequest; the
        // separate values are mapped the same way should a runtime ever use them.
        CoreWebView2WebResourceContext.XmlHttpRequest
            or CoreWebView2WebResourceContext.Fetch
            or CoreWebView2WebResourceContext.EventSource => "xmlhttprequest",
        CoreWebView2WebResourceContext.Websocket => "websocket",
        CoreWebView2WebResourceContext.Ping => "ping",
        CoreWebView2WebResourceContext.CspViolationReport => "csp_report",
        _ => "other",
    };

    /// <summary>
    /// An address in a form that compares equal however the two events spell it: requests come
    /// normalised, with the host in punycode and no fragment; a navigation may come as it was
    /// asked for.
    /// </summary>
    private static string DocumentKey(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            var port = parsed.IsDefaultPort ? "" : $":{parsed.Port}";
            return $"{parsed.Scheme}://{parsed.IdnHost}{port}{parsed.PathAndQuery}";
        }
        var hash = url.IndexOf('#');
        return hash < 0 ? url : url[..hash];
    }

    private void ReportLoadState(CoreWebView2 core, bool loading)
    {
        isLoading = loading;
        Raise(() => LoadStateChanged?.Invoke(new ChromiumLoadState(loading, core.CanGoBack, core.CanGoForward)));
    }

    /// <summary>Runs an action against the browser if there is one to run it against.</summary>
    private void WithCore(Action<CoreWebView2> action)
    {
        if (isClosed || !isReady || View.CoreWebView2 is not { } core) return;
        try
        {
            action(core);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            // The browser process died between the user's click and this call. ProcessFailed
            // reports that to the model; the click itself is simply lost.
        }
    }

    /// <summary>Raises an event unless the browser has been closed.</summary>
    private void Raise(Action raise)
    {
        if (!isClosed) raise();
    }
}
