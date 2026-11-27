using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Mosaic.Core.Browser;

namespace Mosaic.App.Chromium;

/// <summary>One Chromium tab: a WebView2 control and the translation of its events for the model.</summary>
/// <remarks>
/// Creating the underlying browser is asynchronous (it may have to start the WebView2 processes),
/// so a browser begins as an empty, hidden control that remembers the address it was asked to
/// load. Everything is reported through events, and only after <see cref="Start"/> has returned,
/// so the model has always subscribed by the time anything is raised.
/// </remarks>
internal sealed class WebView2Browser : IChromiumBrowser
{
    private readonly WebView2Engine engine;
    private string pendingUrl;
    private bool isReady;
    private bool isClosed;
    private bool isLoading = true;

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
        // Collapsed, not covered: the control is a child window and would draw over any WPF
        // content placed on top of it. WebView2 also stops rendering while it is not visible.
        set => View.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public event Action<string>? TitleChanged;
    public event Action<string>? UrlChanged;
    public event Action<ChromiumLoadState>? LoadStateChanged;
    public event Action<string>? NewTabRequested;
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
            core.SourceChanged += (_, _) => Raise(() => UrlChanged?.Invoke(core.Source));
            core.NavigationStarting += (_, _) => ReportLoadState(core, loading: true);
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
        if (!isClosed && View.IsVisible) View.Focus();
    }

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        engine.BrowserClosed(this);
        try
        {
            View.Dispose();
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
