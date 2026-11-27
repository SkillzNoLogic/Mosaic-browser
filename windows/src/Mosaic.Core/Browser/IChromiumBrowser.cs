namespace Mosaic.Core.Browser;

/// <summary>What a Chromium tab reports whenever a navigation starts, finishes or changes its history.</summary>
public readonly record struct ChromiumLoadState(bool IsLoading, bool CanGoBack, bool CanGoForward);

/// <summary>
/// One Chromium tab's browser, as the model sees it. The app implements this around a WebView2
/// control; tests use a fake.
/// </summary>
/// <remarks>
/// All members are called, and all events raised, on the UI thread. No member throws: a browser
/// that cannot work reports it through <see cref="Failed"/>.
/// </remarks>
public interface IChromiumBrowser
{
    /// <summary>
    /// Whether the page is what the content area shows. The model keeps this true for at most one
    /// browser: the selected tab's, and only while none of Mosaic's own views (start page, error
    /// message) occupies the content area. Web content is a native child window that always draws
    /// above WPF content, so it has to be hidden rather than covered.
    /// </summary>
    bool IsShown { get; set; }

    void Load(string url);
    void GoBack();
    void GoForward();
    void Reload();
    void Stop();

    /// <summary>Gives keyboard focus to the page.</summary>
    void Focus();

    /// <summary>Releases the browser. No events are raised afterwards.</summary>
    void Close();

    event Action<string>? TitleChanged;
    event Action<string>? UrlChanged;
    event Action<ChromiumLoadState>? LoadStateChanged;

    /// <summary>The page asked for a new window (<c>target=_blank</c>, <c>window.open</c>); Mosaic opens a tab instead.</summary>
    event Action<string>? NewTabRequested;

    /// <summary>
    /// The browser could not be created or its process died. The argument is a user-presentable
    /// reason. The browser is unusable afterwards; the model closes it and shows the reason.
    /// </summary>
    event Action<string>? Failed;
}

/// <summary>The Chromium engine as a whole: whether it can be used, and a factory for tab browsers.</summary>
public interface IChromiumEngine
{
    /// <summary>False when the engine cannot run on this machine (WebView2 Runtime missing).</summary>
    bool IsAvailable { get; }

    /// <summary>User-presentable explanation, with a remedy, when <see cref="IsAvailable"/> is false.</summary>
    string? UnavailableReason { get; }

    /// <summary>Creates a browser and starts loading <paramref name="url"/> in it. Only called while available.</summary>
    IChromiumBrowser CreateBrowser(string url);
}
