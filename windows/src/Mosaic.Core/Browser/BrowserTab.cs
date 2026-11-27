namespace Mosaic.Core.Browser;

/// <summary>
/// One tab. A tab is bound to a single engine at a time and can be moved to the other one,
/// which reloads its page there.
/// </summary>
/// <remarks>Only <see cref="BrowserModel"/> changes a tab; everything else observes it.</remarks>
public sealed class BrowserTab : ObservableObject
{
    private EngineKind engine;
    private string url = "";
    private string title = "";
    private bool isLoading;
    private bool canGoBack;
    private bool canGoForward;
    private string? failure;
    private int? geckoId;
    private IChromiumBrowser? chromium;
    private bool isSelected;
    private int blockedCount;

    internal BrowserTab(EngineKind engine) => this.engine = engine;

    public Guid Id { get; } = Guid.NewGuid();

    public EngineKind Engine
    {
        get => engine;
        internal set
        {
            if (!Set(ref engine, value)) return;
            Notify(nameof(EngineHint));
            Notify(nameof(AccessibleName));
        }
    }

    /// <summary>Empty for a tab still showing Mosaic's start page.</summary>
    public string Url
    {
        get => url;
        internal set
        {
            if (!Set(ref url, value)) return;
            Notify(nameof(IsStartPage));
            NotifyTitle();
        }
    }

    public string Title
    {
        get => title;
        internal set
        {
            if (Set(ref title, value)) NotifyTitle();
        }
    }

    public bool IsLoading { get => isLoading; internal set => Set(ref isLoading, value); }
    public bool CanGoBack { get => canGoBack; internal set => Set(ref canGoBack, value); }
    public bool CanGoForward { get => canGoForward; internal set => Set(ref canGoForward, value); }

    /// <summary>Requests the content blocker stopped since this tab's page began loading.</summary>
    public int BlockedCount { get => blockedCount; internal set => Set(ref blockedCount, value); }

    /// <summary>Why the page cannot be shown; null while it can.</summary>
    public string? Failure { get => failure; internal set => Set(ref failure, value); }

    /// <summary>Gecko: the agent's ID for this tab, once Gecko has created it.</summary>
    public int? GeckoId { get => geckoId; internal set => Set(ref geckoId, value); }

    /// <summary>Chromium: this tab's browser.</summary>
    public IChromiumBrowser? Chromium { get => chromium; internal set => Set(ref chromium, value); }

    /// <summary>Maintained by the model so the tab strip can highlight without a selection control.</summary>
    public bool IsSelected { get => isSelected; internal set => Set(ref isSelected, value); }

    public bool IsStartPage => url.Length == 0;

    /// <summary>The label on the tab: the page title, falling back to the host, then the raw address.</summary>
    public string DisplayTitle
    {
        get
        {
            if (IsStartPage) return "New Tab";
            if (title.Length > 0) return title;
            return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 ? parsed.Host : url;
        }
    }

    /// <summary>Tooltip of the engine dot.</summary>
    public string EngineHint => $"Rendered by {engine.DisplayName()} ({engine.EngineName()})";

    /// <summary>What a screen reader announces for the tab.</summary>
    public string AccessibleName => $"{DisplayTitle}, {engine.DisplayName()}";

    /// <summary>Gecko: the tail of this tab's open/navigate chain, so a Gecko tab is never created twice.</summary>
    internal Task? GeckoWork { get; set; }

    /// <summary>
    /// Bumped whenever the tab lets go of whatever it holds in Gecko. Work started before the bump
    /// sees a different number when it resumes and must not touch the tab.
    /// </summary>
    internal int GeckoGeneration { get; set; }

    internal DateTimeOffset LastLoadRequest { get; set; } = DateTimeOffset.MinValue;

    private void NotifyTitle()
    {
        Notify(nameof(DisplayTitle));
        Notify(nameof(AccessibleName));
    }
}
