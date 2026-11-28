using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Permissions;

namespace Mosaic.Core.Engine;

/// <summary>Where the engine area keeps its files, and switches for test runs.</summary>
public sealed class PageFeaturesOptions
{
    /// <summary>Folder of <c>SitePermissions.json</c> and <c>SiteZoom.json</c>.</summary>
    public string DataDirectory { get; init; } = AppPaths.DataRoot;

    /// <summary>
    /// Test runs only (<c>MOSAIC_FAKE_MEDIA=1</c>): fake camera, microphone and location in Gecko,
    /// so no system dialog appears and nothing real is used.
    /// </summary>
    public bool FakeMedia { get; init; } = Environment.GetEnvironmentVariable("MOSAIC_FAKE_MEDIA") == "1";

    /// <summary>Reads Windows' privacy switches; replaceable in tests.</summary>
    public Func<WindowsCapability, WindowsCapabilityAccess> WindowsAccess { get; init; } = WindowsPrivacy.Read;

    /// <summary>How often Gecko's zoom and mute are compared with Mosaic's while it runs.</summary>
    public TimeSpan GeckoActivityInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Where a screenshot goes.</summary>
public enum ScreenshotTarget
{
    /// <summary>Pictures\Screenshots, named after the page and the time.</summary>
    Screenshots,
    /// <summary>A save picker.</summary>
    Ask,
    Clipboard,
    /// <summary>The path given with the request.</summary>
    File,
}

/// <summary>
/// Per-page features in both engines: find in page, per-site zoom, printing and PDF, developer
/// tools, mute, screenshots, Picture in Picture, page full screen, Mosaic's own permission
/// prompts, pop-up blocking and JavaScript dialogs. Port of
/// <c>Sources/Mosaic/Features/Engine/PageFeatures*.swift</c>.
/// </summary>
/// <remarks>
/// <para>Owned by the model (<c>BrowserModel.Pages</c>). The UI (find bar, prompts, notices,
/// dialogs, menus) is <c>Features/Engine</c> in the app, which also provides each Chromium tab's
/// <see cref="IChromiumPage"/> (<see cref="ChromiumPages"/>) and the window services
/// (<see cref="Host"/>). Gecko is driven through the shared agent's page-feature methods.</para>
/// <para>UI thread only, like the model.</para>
/// </remarks>
public sealed partial class PageFeatures : ObservableObject
{
    /// <summary>Tallest page a full-page screenshot covers, in CSS pixels.</summary>
    public const int MaxScreenshotHeight = 16384;

    private static readonly TimeSpan ZoomReportGrace = TimeSpan.FromSeconds(1.5);

    private readonly BrowserModel model;
    private readonly PageFeaturesOptions options;
    private readonly Dictionary<Guid, TabState> states = [];
    private SitePermissionStore? permissions;
    private SiteZoomStore? zoomStore;

    // Find
    private bool isFindBarVisible;
    private string findText = "";
    private bool findMatchCase;
    private FindStatus? findStatus;
    private Guid? findTabId;
    private bool findStarted;
    private int findGeneration;

    // What the overlays show for the selected tab
    private PermissionPrompt? currentPrompt;
    private PopupNotice? currentPopupNotice;
    private PageDialog? currentDialog;
    private bool showsFindBar;
    private Guid? fullScreenTabId;

    private sealed class TabState
    {
        public double Zoom = 1;
        public bool Muted;
        /// <summary>The mute was set through <see cref="ApplyMute"/>, so Mosaic keeps Gecko to it.</summary>
        public bool MuteManaged;
        public bool PlayingAudio;
        public string? LastSite;
        public DateTimeOffset ZoomAppliedAt = DateTimeOffset.MinValue;
        public PropertyChangedEventHandler? Observer;
    }

    internal PageFeatures(BrowserModel model, PageFeaturesOptions options)
    {
        this.model = model;
        this.options = options;
        model.Tabs.CollectionChanged += TabsChanged;
        model.PropertyChanged += ModelChanged;
        foreach (var tab in model.Tabs) Observe(tab);
        model.Gecko.SnapshotReceived += GeckoSnapshot;
        model.Gecko.StatusChanged += GeckoStatusChanged;
        if (model.Gecko.Status == GeckoStatus.Running) GeckoStatusChanged();
    }

    /// <summary>Remembered per-site permission decisions, shared with the privacy area's settings page.</summary>
    public SitePermissionStore Permissions
    {
        get
        {
            if (permissions is null)
            {
                permissions = new SitePermissionStore(Path.Combine(options.DataDirectory, "SitePermissions.json"));
                permissions.Changed += PermissionChanged;
            }
            return permissions;
        }
    }

    /// <summary>Remembered per-site zoom, shared by both engines.</summary>
    public SiteZoomStore ZoomStore => zoomStore ??= new SiteZoomStore(Path.Combine(options.DataDirectory, "SiteZoom.json"));

    /// <summary>The window's services; set by the app at start-up.</summary>
    public IPageFeaturesHost? Host { get; set; }

    /// <summary>A Chromium tab's page features; set by the app (null for tabs without a browser yet).</summary>
    public Func<BrowserTab, IChromiumPage?>? ChromiumPages { get; set; }

    /// <summary>Applies a site decision to Chromium's own permission settings; set by the app.</summary>
    public Action<SitePermission, SitePermissionDecision?, string>? ChromiumPermissionMirror { get; set; }

    /// <summary>The find field should take keyboard focus and select its text.</summary>
    public event Action? FindFocusRequested;

    /// <summary>A tab's audio started or stopped (Chromium; the tabs area shows it).</summary>
    public event Action<BrowserTab>? AudioChanged;

    /// <summary>The page in front entered or left full screen (<see cref="IsPageFullScreen"/>).</summary>
    public event Action<bool>? PageFullScreenChanged;

    internal BrowserTab? SelectedTab => model.SelectedTab;

    internal BrowserTab? Tab(Guid? id) => id is { } value ? model.Tabs.FirstOrDefault(tab => tab.Id == value) : null;

    internal BrowserTab? TabForGeckoId(int? id) =>
        id is { } value ? model.Tabs.FirstOrDefault(tab => tab.Engine == EngineKind.Gecko && tab.GeckoId == value) : null;

    private TabState StateOf(BrowserTab tab)
    {
        if (!states.TryGetValue(tab.Id, out var state)) states[tab.Id] = state = new TabState();
        return state;
    }

    internal IChromiumPage? ChromiumPage(BrowserTab tab) =>
        tab is { Engine: EngineKind.Chromium, Chromium: not null } ? ChromiumPages?.Invoke(tab) : null;

    /// <summary>True when the tab shows a web page an engine can act on.</summary>
    public bool IsWebPage(BrowserTab? tab)
    {
        if (tab is null || tab.IsStartPage || tab.Failure is not null) return false;
        if (tab.Url.StartsWith("mosaic:", StringComparison.OrdinalIgnoreCase)) return false;
        return tab.Engine == EngineKind.Chromium
            ? tab.Chromium is not null
            : tab.GeckoId is not null && model.Gecko.Status == GeckoStatus.Running;
    }

    private bool Refuse()
    {
        Host?.Beep();
        return false;
    }

    // ---- Tabs ----

    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs change)
    {
        var present = model.Tabs.Select(tab => tab.Id).ToHashSet();
        foreach (var tab in model.Tabs)
        {
            if (!states.TryGetValue(tab.Id, out var state) || state.Observer is null) Observe(tab);
        }
        foreach (var id in states.Keys.Where(id => !present.Contains(id)).ToList()) TabClosed(id, change);
        UpdateOverlays();
    }

    private void TabClosed(Guid id, NotifyCollectionChangedEventArgs change)
    {
        var closed = change.OldItems?.OfType<BrowserTab>().FirstOrDefault(tab => tab.Id == id);
        if (closed is not null && states[id].Observer is { } observer) closed.PropertyChanged -= observer;
        states.Remove(id);
        DropPageState(id, DropReason.Closed);
        if (fullScreenTabId == id) EndFullScreen();
        if (findTabId == id) HideFindBar();
    }

    private void Observe(BrowserTab tab)
    {
        var state = StateOf(tab);
        state.Observer = (_, change) => TabPropertyChanged(tab, change.PropertyName);
        tab.PropertyChanged += state.Observer;
        if (tab.Url.Length > 0) UrlChanged(tab);
    }

    private void TabPropertyChanged(BrowserTab tab, string? property)
    {
        if (!states.ContainsKey(tab.Id)) return;
        switch (property)
        {
            case nameof(BrowserTab.Url):
                UrlChanged(tab);
                break;
            case nameof(BrowserTab.Engine):
                StateOf(tab).PlayingAudio = false;
                DropPageState(tab.Id, DropReason.EngineSwitched);
                if (findTabId == tab.Id) HideFindBar();
                if (fullScreenTabId == tab.Id) EndFullScreen();
                UpdateOverlays();
                break;
            case nameof(BrowserTab.GeckoId) when tab.GeckoId is not null:
            case nameof(BrowserTab.Chromium) when tab.Chromium is not null:
                EngineAttached(tab);
                break;
            case nameof(BrowserTab.Failure) or nameof(BrowserTab.IsLoading):
                UpdateOverlays();
                break;
        }
    }

    /// <summary>The tab's page now lives in an engine: bring mute and zoom in line.</summary>
    private void EngineAttached(BrowserTab tab)
    {
        var state = StateOf(tab);
        if (state.MuteManaged && state.Muted) ApplyMuteToEngine(tab, true);
        ApplyZoom(ZoomStore.Factor(tab.Url), tab);
    }

    private void UrlChanged(BrowserTab tab)
    {
        var url = tab.Url;
        var state = StateOf(tab);
        var site = PageZoom.Site(url) ?? SitePermissionStore.Site(url) ?? url;
        if (state.LastSite == site) return;
        var hadSite = state.LastSite is not null;
        state.LastSite = site;
        // Prompts and pop-up notices belong to the page that asked.
        if (hadSite) DropPageState(tab.Id, DropReason.Navigated, SitePermissionStore.Site(url));
        var factor = ZoomStore.Factor(url);
        if (Math.Abs(factor - state.Zoom) > 0.005 || !PageZoom.IsDefault(factor)) ApplyZoom(factor, tab);
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName != nameof(BrowserModel.SelectedTab)) return;
        var id = model.SelectedTab?.Id;
        if (isFindBarVisible && findTabId != id) HideFindBar();
        if (fullScreenTabId is { } full && full != id) EndFullScreen();
        UpdateOverlays();
        NotifyZoom();
    }

    // ---- What the overlays show ----

    /// <summary>The selected tab's first permission prompt.</summary>
    public PermissionPrompt? CurrentPrompt { get => currentPrompt; private set => Set(ref currentPrompt, value); }

    /// <summary>The selected tab's blocked-pop-up notice.</summary>
    public PopupNotice? CurrentPopupNotice { get => currentPopupNotice; private set => Set(ref currentPopupNotice, value); }

    /// <summary>The selected tab's first JavaScript dialog.</summary>
    public PageDialog? CurrentDialog { get => currentDialog; private set => Set(ref currentDialog, value); }

    /// <summary>The find bar is open for the selected tab.</summary>
    public bool ShowsFindBar { get => showsFindBar; private set => Set(ref showsFindBar, value); }

    /// <summary>True while any overlay draws over the selected tab's page (Gecko's window must be hidden then).</summary>
    public bool HasOverlay => showsFindBar || currentPrompt is not null || currentPopupNotice is not null || currentDialog is not null;

    private void UpdateOverlays()
    {
        var selected = model.SelectedTab?.Id;
        var before = HasOverlay;
        // Assigned as a whole each time: an item whose content changed (a merged prompt, one
        // more pop-up) is announced again so the view redraws it.
        var prompt = prompts.FirstOrDefault(item => item.TabId == selected);
        if (ReferenceEquals(prompt, currentPrompt)) Notify(nameof(CurrentPrompt));
        CurrentPrompt = prompt;
        var notice = popupNotices.FirstOrDefault(item => item.TabId == selected);
        if (ReferenceEquals(notice, currentPopupNotice)) Notify(nameof(CurrentPopupNotice));
        CurrentPopupNotice = notice;
        CurrentDialog = dialogs.FirstOrDefault(item => item.TabId == selected);
        ShowsFindBar = isFindBarVisible && findTabId == selected;
        if (before != HasOverlay) Notify(nameof(HasOverlay));
    }

    // ---- Find in page ----

    public bool IsFindBarVisible { get => isFindBarVisible; private set => Set(ref isFindBarVisible, value); }

    /// <summary>The find field. Changing it searches again from the top.</summary>
    public string FindText
    {
        get => findText;
        set
        {
            if (Set(ref findText, value ?? "")) FindQueryChanged();
        }
    }

    public bool FindMatchCase
    {
        get => findMatchCase;
        set
        {
            if (Set(ref findMatchCase, value)) FindQueryChanged();
        }
    }

    public FindStatus? FindStatus
    {
        get => findStatus;
        private set
        {
            if (Set(ref findStatus, value)) Notify(nameof(FindLabel));
        }
    }

    /// <summary>"3 of 12"; empty while there is nothing to say.</summary>
    public string FindLabel => findStatus is { } status && findText.Length > 0 ? status.Label : "";

    /// <summary>Ctrl+F: shows the find bar for the selected tab and focuses it.</summary>
    public void ShowFindBar()
    {
        if (SelectedTab is not { } tab || !IsWebPage(tab))
        {
            Refuse();
            return;
        }
        if (findTabId != tab.Id)
        {
            if (Tab(findTabId) is { } old) StopFinding(old);
            findTabId = tab.Id;
            FindStatus = null;
            findStarted = false;
        }
        IsFindBarVisible = true;
        UpdateOverlays();
        FindFocusRequested?.Invoke();
        if (findText.Length > 0) _ = SearchAsync(again: false);
    }

    /// <summary>Esc / the close button: hides the bar, clears highlights, gives the page focus back.</summary>
    public void CloseFindBar()
    {
        var tab = Tab(findTabId);
        HideFindBar();
        if (tab is not null && ReferenceEquals(tab, SelectedTab)) model.FocusContent();
    }

    private void HideFindBar()
    {
        if (Tab(findTabId) is { } tab) StopFinding(tab);
        IsFindBarVisible = false;
        findTabId = null;
        FindStatus = null;
        findStarted = false;
        findGeneration++;
        UpdateOverlays();
    }

    private void FindQueryChanged()
    {
        Notify(nameof(FindLabel));
        findStarted = false;
        _ = SearchAsync(again: false);
    }

    /// <summary>F3 / Shift+F3, Enter in the field and the bar's arrows.</summary>
    public void FindNext(bool backwards = false)
    {
        if (!isFindBarVisible || findTabId != SelectedTab?.Id)
        {
            ShowFindBar();
            if (findText.Length == 0) return;
        }
        _ = SearchAsync(again: true, backwards);
    }

    private async Task SearchAsync(bool again, bool backwards = false)
    {
        if (Tab(findTabId) is not { } tab || !IsWebPage(tab)) return;
        var generation = ++findGeneration;
        var text = findText;
        if (text.Length == 0)
        {
            FindStatus = null;
            StopFinding(tab);
            return;
        }
        // A repeat of a search that never ran is a new search.
        var next = again && findStarted;
        findStarted = true;
        FindStatus? result = null;
        try
        {
            if (tab.Engine == EngineKind.Chromium)
            {
                if (ChromiumPage(tab) is { } page) result = await page.FindAsync(text, findMatchCase, next, backwards);
            }
            else if (tab.GeckoId is { } id)
            {
                var reply = await model.Gecko.CallAsync("find", id, text, new System.Text.Json.Nodes.JsonObject
                {
                    ["again"] = next,
                    ["backwards"] = backwards,
                    ["caseSensitive"] = findMatchCase,
                });
                if (reply is System.Text.Json.Nodes.JsonObject json)
                {
                    result = new FindStatus(Int(json["current"]), Int(json["total"]), Int(json["limit"]));
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or RdpException)
        {
            // Gecko went away or the tab closed under the search; the bar stays as it was.
            return;
        }
        if (generation == findGeneration && result is { } status) FindStatus = status;
    }

    private void StopFinding(BrowserTab tab)
    {
        if (tab.Engine == EngineKind.Chromium) ChromiumPage(tab)?.StopFind();
        else if (tab.GeckoId is { } id) model.Gecko.Send("stopFind", id);
    }

    internal static int Int(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out double number) ? (int)number : 0;

    // ---- Zoom ----

    /// <summary>The zoom of a tab's page (1 = 100 %).</summary>
    public double ZoomOf(BrowserTab tab) => states.TryGetValue(tab.Id, out var state) ? state.Zoom : 1;

    /// <summary>The selected page's zoom as a label, "110 %".</summary>
    public string ZoomLabel => PageZoom.PercentLabel(SelectedTab is { } tab ? ZoomOf(tab) : 1);

    public bool CanZoomIn => IsWebPage(SelectedTab) && ZoomOf(SelectedTab!) < PageZoom.Maximum - 0.001;
    public bool CanZoomOut => IsWebPage(SelectedTab) && ZoomOf(SelectedTab!) > PageZoom.Minimum + 0.001;
    public bool CanResetZoom => IsWebPage(SelectedTab) && !PageZoom.IsDefault(ZoomOf(SelectedTab!));

    public void ZoomIn() => SetZoom(PageZoom.ZoomedIn(SelectedTab is { } tab ? ZoomOf(tab) : 1));
    public void ZoomOut() => SetZoom(PageZoom.ZoomedOut(SelectedTab is { } tab ? ZoomOf(tab) : 1));
    public void ResetZoom() => SetZoom(1);

    /// <summary>
    /// Zooms the selected page and remembers the zoom for its site: every open tab on the site
    /// follows, in both engines.
    /// </summary>
    public void SetZoom(double factor)
    {
        if (SelectedTab is not { } tab || !IsWebPage(tab))
        {
            Refuse();
            return;
        }
        var value = PageZoom.Clamped(factor);
        ZoomStore.SetFactor(value, tab.Url);
        var site = PageZoom.Site(tab.Url);
        foreach (var other in model.Tabs)
        {
            if (ReferenceEquals(other, tab) || (site is not null && PageZoom.Site(other.Url) == site)) ApplyZoom(value, other);
        }
        model.Notice = $"Zoom {PageZoom.PercentLabel(value)}";
    }

    private void ApplyZoom(double factor, BrowserTab tab)
    {
        var state = StateOf(tab);
        state.Zoom = factor;
        state.ZoomAppliedAt = model.Now;
        if (tab.Engine == EngineKind.Chromium) ChromiumPage(tab)?.SetZoom(factor);
        else if (tab.GeckoId is { } id) model.Gecko.Send("setZoom", id, factor);
        if (ReferenceEquals(tab, SelectedTab)) NotifyZoom();
    }

    private void NotifyZoom()
    {
        Notify(nameof(ZoomLabel));
        Notify(nameof(CanZoomIn));
        Notify(nameof(CanZoomOut));
        Notify(nameof(CanResetZoom));
    }

    // ---- Mute and audio ----

    /// <summary>
    /// Mutes or unmutes a tab's page in whichever engine it uses, and keeps it so when the page
    /// moves to the other engine. The tabs area owns the mute state and calls this.
    /// </summary>
    public void ApplyMute(BrowserTab tab, bool muted)
    {
        var state = StateOf(tab);
        state.Muted = muted;
        state.MuteManaged = true;
        ApplyMuteToEngine(tab, muted);
    }

    /// <summary>What Mosaic last told the engines about the tab's sound.</summary>
    public bool IsMuted(BrowserTab tab) => states.TryGetValue(tab.Id, out var state) && state.Muted;

    private void ApplyMuteToEngine(BrowserTab tab, bool muted)
    {
        if (tab.Engine == EngineKind.Chromium) ChromiumPage(tab)?.SetMuted(muted);
        else if (tab.GeckoId is { } id) model.Gecko.Send("setMuted", id, muted);
    }

    /// <summary>Whether the tab's page is playing sound (as Chromium reports it).</summary>
    public bool IsPlayingAudio(BrowserTab tab) => states.TryGetValue(tab.Id, out var state) && state.PlayingAudio;

    /// <summary>Chromium: the page started or stopped playing sound.</summary>
    public void ChromiumAudioChanged(BrowserTab tab, bool playing)
    {
        if (!states.TryGetValue(tab.Id, out var state) || state.PlayingAudio == playing) return;
        state.PlayingAudio = playing;
        AudioChanged?.Invoke(tab);
    }

    // ---- Printing, PDF, developer tools, Picture in Picture ----

    /// <summary>Ctrl+P: the engine's print dialog for the page in front.</summary>
    public bool Print()
    {
        if (SelectedTab is not { } tab || !IsWebPage(tab)) return Refuse();
        if (tab.Engine == EngineKind.Chromium)
        {
            ChromiumPage(tab)?.Print();
        }
        else if (tab.GeckoId is { } id)
        {
            // Firefox's print dialog is tab-modal inside the Gecko window, which needs the keyboard.
            model.Gecko.Send("print", id);
            model.FocusContent();
        }
        return true;
    }

    /// <summary>
    /// Saves the page in front as a PDF: at <paramref name="path"/>, or where the person picks
    /// (null). Returns where it was saved, or null.
    /// </summary>
    public async Task<string?> ExportPdfAsync(string? path = null)
    {
        if (SelectedTab is not { } tab || !IsWebPage(tab))
        {
            Refuse();
            return null;
        }
        path ??= Host is { } host ? await host.AskSavePathAsync(PageText.FileName(tab, null), "pdf") : null;
        if (path is null) return null;
        var saved = false;
        try
        {
            if (tab.Engine == EngineKind.Chromium)
            {
                if (ChromiumPage(tab) is { } page) saved = await page.PrintToPdfAsync(path);
            }
            else if (tab.GeckoId is { } id)
            {
                await model.Gecko.CallAsync("saveAsPDF", id, path);
                saved = File.Exists(path);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or RdpException or IOException)
        {
            saved = false;
        }
        model.Notice = saved ? $"Saved “{Path.GetFileName(path)}”" : "Couldn’t save the PDF.";
        return saved ? path : null;
    }

    /// <summary>Opens the page's developer tools (Gecko: toggles them, docked under the page).</summary>
    public bool ToggleDevTools()
    {
        if (SelectedTab is not { } tab || !IsWebPage(tab)) return Refuse();
        if (tab.Engine == EngineKind.Chromium) ChromiumPage(tab)?.OpenDevTools();
        else if (tab.GeckoId is { } id) model.Gecko.Send("toggleDevTools", id);
        return true;
    }

    /// <summary>
    /// Puts the page's video into Picture in Picture (or brings it back). Both engines pick the
    /// focused video, else the one playing, else the longest.
    /// </summary>
    public async Task TogglePictureInPictureAsync()
    {
        if (SelectedTab is not { } tab || !IsWebPage(tab))
        {
            Refuse();
            return;
        }
        if (tab.Engine == EngineKind.Gecko)
        {
            if (tab.GeckoId is { } id) model.Gecko.Send("togglePictureInPicture", id);
            return;
        }
        if (ChromiumPage(tab) is not { } page) return;
        var outcome = await page.TogglePictureInPictureAsync();
        if (outcome == "none") model.Notice = "This page has no video to show in Picture in Picture.";
        else if (outcome == "unsupported") model.Notice = "Picture in Picture isn’t available in Chromium tabs here. Switch the tab to Mozilla to use it.";
    }

    // ---- Screenshots ----

    /// <summary>
    /// Captures the visible area or the whole page of the tab in front and puts it where
    /// <paramref name="target"/> says (<paramref name="path"/> for <see cref="ScreenshotTarget.File"/>).
    /// Returns the picture and where it went (null when nothing was saved), or null on failure.
    /// </summary>
    public async Task<(PageCapture Capture, string? SavedTo)?> TakeScreenshotAsync(
        bool fullPage, ScreenshotTarget target = ScreenshotTarget.Screenshots, string? path = null)
    {
        if (SelectedTab is not { } tab || !IsWebPage(tab))
        {
            Refuse();
            return null;
        }
        var capture = await CaptureAsync(tab, fullPage);
        if (capture is null)
        {
            model.Notice = "Couldn’t take a screenshot of this page.";
            return null;
        }
        var note = capture.Cropped ? " (cut off: the page is very long)" : "";
        if (target == ScreenshotTarget.Clipboard)
        {
            var copied = Host is { } clipboard && await clipboard.CopyImageAsync(capture.Png);
            model.Notice = copied ? "Screenshot copied" + note : "Couldn’t copy the screenshot.";
            return copied ? (capture, null) : null;
        }

        var name = PageText.FileName(tab, fullPage ? "full page" : null);
        path = target switch
        {
            ScreenshotTarget.File => path,
            ScreenshotTarget.Ask => Host is { } host ? await host.AskSavePathAsync(name, "png") : null,
            _ => Path.Combine(Host?.ScreenshotsFolder ?? DefaultScreenshotsFolder,
                $"{name} {model.Now.ToLocalTime().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture)}.png"),
        };
        if (path is null) return null;
        try
        {
            var bytes = capture.Png;
            await Task.Run(() =>
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
                File.WriteAllBytes(path, bytes);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            model.Notice = "Couldn’t save the screenshot.";
            return null;
        }
        model.Notice = target == ScreenshotTarget.Screenshots
            ? $"Screenshot saved to Pictures › Screenshots{note}"
            : $"Saved “{Path.GetFileName(path)}”{note}";
        return (capture, path);
    }

    /// <summary>Pictures\Screenshots, where Windows' own screenshots go.</summary>
    public static string DefaultScreenshotsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    private async Task<PageCapture?> CaptureAsync(BrowserTab tab, bool fullPage)
    {
        if (tab.Engine == EngineKind.Chromium) return ChromiumPage(tab) is { } page ? await page.CaptureAsync(fullPage) : null;
        if (tab.GeckoId is not { } id) return null;
        var file = Path.Combine(Path.GetTempPath(), $"mosaic-screenshot-{Guid.NewGuid():N}.png");
        try
        {
            var info = await model.Gecko.CallAsync("screenshot", id, file, fullPage) as System.Text.Json.Nodes.JsonObject;
            if (info is null || !File.Exists(file)) return null;
            var png = await File.ReadAllBytesAsync(file);
            var cropped = info["cropped"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out bool flag) && flag;
            return new PageCapture(png, Int(info["width"]), Int(info["height"]), cropped);
        }
        catch (Exception error) when (error is InvalidOperationException or RdpException or IOException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Left in the temporary folder; Windows cleans it.
            }
        }
    }

    // ---- Page full screen ----

    /// <summary>The tab whose page is in full screen (JavaScript Fullscreen API), if any.</summary>
    public Guid? FullScreenTabId
    {
        get => fullScreenTabId;
        private set
        {
            var was = fullScreenTabId is not null;
            if (!Set(ref fullScreenTabId, value)) return;
            var now = value is not null;
            if (was == now) return;
            Notify(nameof(IsPageFullScreen));
            Host?.SetPageFullScreen(now);
            PageFullScreenChanged?.Invoke(now);
        }
    }

    /// <summary>
    /// A page is in full screen: Mosaic's window fills the screen and its own chrome (toolbar,
    /// tabs, sidebar) is hidden so the page area covers everything.
    /// </summary>
    public bool IsPageFullScreen => fullScreenTabId is not null;

    /// <summary>Chromium: the page entered or left full screen (WebView2's ContainsFullScreenElement).</summary>
    public void ChromiumFullScreenChanged(BrowserTab tab, bool fullScreen)
    {
        if (fullScreen) FullScreenTabId = tab.Id;
        else if (fullScreenTabId == tab.Id) FullScreenTabId = null;
        if (!fullScreen && ReferenceEquals(tab, SelectedTab)) model.FocusContent();
    }

    /// <summary>Leaves page full screen in whichever engine the tab uses.</summary>
    public void EndFullScreen()
    {
        if (fullScreenTabId is not { } id) return;
        var tab = Tab(id);
        FullScreenTabId = null;
        if (tab is null) return;
        if (tab.Engine == EngineKind.Chromium) ChromiumPage(tab)?.ExitFullScreen();
        else if (model.Gecko.Status == GeckoStatus.Running) model.Gecko.Send("exitFullscreen");
    }
}
