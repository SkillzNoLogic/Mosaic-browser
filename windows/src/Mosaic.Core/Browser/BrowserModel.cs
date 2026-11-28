using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Mosaic.Core.Extensions;
using Mosaic.Core.Gecko;
using Mosaic.Core.Search;

namespace Mosaic.Core.Browser;

/// <summary>Things the user can ask of the browser from the keyboard; see <see cref="Shortcuts"/>.</summary>
public enum BrowserCommand
{
    NewTab,
    CloseTab,
    FocusAddress,
    Reload,
    Back,
    Forward,
    NextTab,
    PreviousTab,
    ToggleEngine,
    NextSearchEngine,
    ShowExtensions,
    ToggleBlocker,
}

/// <summary>What the address box currently holds, for its leading icon.</summary>
public enum AddressKind
{
    /// <summary>Empty, or text that will be searched.</summary>
    Search,
    /// <summary>An <c>https://</c> address.</summary>
    Secure,
    /// <summary>Any other address.</summary>
    Web,
}

/// <summary>What the content area shows for the selected tab.</summary>
public enum ContentKind
{
    StartPage,
    /// <summary>The page cannot be shown; <see cref="BrowserTab.Failure"/> says why.</summary>
    Failure,
    ChromiumPage,
    ChromiumStarting,
    GeckoPage,
    GeckoStarting,
    /// <summary>Gecko is running but has not created this tab yet.</summary>
    GeckoOpening,
}

/// <summary>How the content blocker's toolbar button looks.</summary>
public enum BlockerShield
{
    /// <summary>The blocker is switched off, or not part of this build.</summary>
    Off,
    /// <summary>On, but not filtering the page in front (a start page, a site the user exempted).</summary>
    Idle,
    /// <summary>Filtering the page in front.</summary>
    Filtering,
}

/// <summary>The text Mosaic shows in the content area in place of a page.</summary>
public sealed record ContentMessage(string Title, string? Detail, bool ShowsProgress, bool IsFailure);

/// <summary>
/// The browser's state: tabs, the selected search engine and rendering engine, and the glue
/// between Mosaic's UI and the two engines.
/// </summary>
/// <remarks>
/// Port of <c>Sources/Mosaic/Model/BrowserModel.swift</c>. Single-threaded: create it and call it
/// on the UI thread, with that thread's <see cref="SynchronizationContext"/> current, which is also
/// where the engines raise their events.
/// </remarks>
public sealed partial class BrowserModel : ObservableObject
{
    private const string BlankPage = "about:blank";

    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(3.5);
    /// <summary>How long after a load request a report of the blank page is taken to be stale.</summary>
    private static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(2);
    /// <summary>How long after the user picks a tab Gecko's own idea of the selection is ignored.</summary>
    private static readonly TimeSpan SelectionGrace = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(150);

    private readonly IChromiumEngine chromium;
    private readonly BrowserSettings settings;
    private readonly BrowserRules rules;
    private readonly TimeProvider time;

    private BrowserTab? selectedTab;
    private EngineKind engine;
    private SearchEngine searchEngine;
    private string addressText = "";
    private AddressKind addressKind;
    private bool showExtensions;
    private bool showBlocker;
    private BlockerShield blockerShield;
    private string blockerBadge = "";
    private string blockerHelp = "";
    private string? blockerSite;
    private bool isBlockingSite;
    private bool isFilteringPage;
    private string blockedCountText = "";
    private string? notice;
    private int noticeVersion;
    private bool isGeckoAttached;
    private bool isShutDown;

    private bool canGoBack;
    private bool canGoForward;
    private bool isLoading;
    private bool canReload;
    private ContentKind content;
    private ContentMessage? message;
    private string? webStoreExtensionId;
    private string addExtensionLabel = "";

    private int pendingGeckoOpens;
    /// <summary>Gecko tabs to switch to as soon as they show up in a snapshot.</summary>
    private readonly HashSet<int> selectWhenAdopted = [];
    /// <summary>The tab Gecko reported as selected in the previous snapshot.</summary>
    private int? lastGeckoSelection;
    /// <summary>
    /// Gecko tabs Mosaic has asked to close; a snapshot taken just before the close lands must not
    /// resurrect them. Agent tab IDs are never reused.
    /// </summary>
    private readonly HashSet<int> closedGeckoIds = [];
    private DateTimeOffset lastLocalSelection = DateTimeOffset.MinValue;

    public BrowserModel(
        IGeckoEngine gecko,
        IChromiumEngine chromium,
        ExtensionManager extensions,
        BrowserSettings settings,
        BrowserRules? rules = null,
        TimeProvider? time = null,
        ContentBlockerOptions? blocker = null)
    {
        Gecko = gecko;
        Extensions = extensions;
        this.chromium = chromium;
        this.settings = settings;
        this.rules = rules ?? BrowserRules.Default;
        this.time = time ?? TimeProvider.System;

        searchEngine = settings.SearchEngineId is { } id ? SearchEngine.Named(id) ?? SearchEngine.Default : SearchEngine.Default;
        // Mozilla is the default engine.
        var saved = settings.Engine ?? EngineKind.Gecko;
        engine = saved == EngineKind.Chromium && !chromium.IsAvailable ? EngineKind.Gecko : saved;

        gecko.SnapshotReceived += Reconcile;
        gecko.Stopped += GeckoStopped;
        gecko.StatusChanged += GeckoStatusChanged;
        Blocker = new ContentBlocker(gecko, settings, blocker, this.time);
        gecko.OnStarted = async () =>
        {
            // The blocker first, so the very first page is already filtered.
            await Blocker.ConfigureGeckoAsync();
            await extensions.LoadAllIntoGeckoAsync();
        };
        // "Add to Mosaic" turns into "Reinstall" once the listing's extension is installed.
        extensions.PropertyChanged += (_, _) => UpdatePresentation();
        // The shield shows the blocker's state.
        Blocker.PropertyChanged += (_, _) => UpdatePresentation();
        Blocker.Start();

        NewTab();
    }

    public IGeckoEngine Gecko { get; }
    public ExtensionManager Extensions { get; }
    public ContentBlocker Blocker { get; }

    public ObservableCollection<BrowserTab> Tabs { get; } = [];

    public BrowserTab? SelectedTab
    {
        get => selectedTab;
        private set
        {
            var previous = selectedTab;
            if (!Set(ref selectedTab, value)) return;
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;
        }
    }

    /// <summary>Engine used for new tabs. Mirrors the selected tab's engine.</summary>
    public EngineKind Engine
    {
        get => engine;
        private set
        {
            if (!Set(ref engine, value)) return;
            Notify(nameof(IsGeckoEngine));
            Notify(nameof(IsChromiumEngine));
            Notify(nameof(StartPageCaption));
        }
    }

    public bool IsGeckoEngine => engine == EngineKind.Gecko;
    public bool IsChromiumEngine => engine == EngineKind.Chromium;

    public bool ChromiumAvailable => chromium.IsAvailable;

    public SearchEngine SearchEngine
    {
        get => searchEngine;
        set
        {
            // A selection control hands back null while its list is being rebuilt.
            if (value is null || !Set(ref searchEngine, value)) return;
            settings.SearchEngineId = value.Id;
            settings.Save();
            Notify(nameof(AddressPlaceholder));
            Notify(nameof(StartPageCaption));
        }
    }

    public string AddressText
    {
        get => addressText;
        set
        {
            if (Set(ref addressText, value ?? "")) UpdateAddressKind();
        }
    }

    public AddressKind AddressKind { get => addressKind; private set => Set(ref addressKind, value); }

    public string AddressPlaceholder => $"Search {searchEngine.Name} or enter address";

    public string StartPageCaption => $"New tabs open in {engine.DisplayName()} · searching with {searchEngine.Name}";

    /// <summary>
    /// True while the user is typing in the address field; page-driven URL changes must not
    /// overwrite what they are typing. Set through <see cref="BeginAddressEditing"/> and
    /// <see cref="EndAddressEditing"/>.
    /// </summary>
    public bool IsEditingAddress { get; private set; }

    /// <summary>
    /// Scripted runs must not pull keyboard focus away from whatever the person at the PC is
    /// doing; automation mode turns this off.
    /// </summary>
    public bool AllowsFocusChanges { get; set; } = true;

    public bool ShowExtensions { get => showExtensions; set => Set(ref showExtensions, value); }

    /// <summary>Whether the content blocker's popover is open.</summary>
    public bool ShowBlocker
    {
        get => showBlocker;
        set
        {
            if (!Set(ref showBlocker, value)) return;
            // "Lists updated 3 hours ago" has aged since the popover was last open.
            if (value) Blocker.RefreshListsLine();
        }
    }

    // The content blocker's toolbar button and popover, for the selected tab.
    public BlockerShield BlockerShield { get => blockerShield; private set => Set(ref blockerShield, value); }

    /// <summary>The count on the shield: requests blocked on the page in front, empty when none or not filtering.</summary>
    public string BlockerBadge { get => blockerBadge; private set => Set(ref blockerBadge, value); }

    /// <summary>The shield's tooltip and accessible value.</summary>
    public string BlockerHelp { get => blockerHelp; private set => Set(ref blockerHelp, value); }

    /// <summary>The site of the page in front, for the per-site switch; null when it is not a web page.</summary>
    public string? BlockerSite
    {
        get => blockerSite;
        private set
        {
            if (!Set(ref blockerSite, value)) return;
            Notify(nameof(HasBlockerSite));
            Notify(nameof(BlockerSiteLabel));
        }
    }

    public bool HasBlockerSite => blockerSite is not null;
    public string BlockerSiteLabel => $"On for {blockerSite}";

    /// <summary>The per-site switch: false when the user turned the blocker off for <see cref="BlockerSite"/>.</summary>
    public bool IsBlockingSite { get => isBlockingSite; private set => Set(ref isBlockingSite, value); }

    /// <summary>Whether requests from the page in front are being filtered right now.</summary>
    public bool IsFilteringPage { get => isFilteringPage; private set => Set(ref isFilteringPage, value); }

    /// <summary>"3 requests blocked on this page".</summary>
    public string BlockedCountText { get => blockedCountText; private set => Set(ref blockedCountText, value); }

    /// <summary>Short-lived message; clears itself after a few seconds.</summary>
    public string? Notice
    {
        get => notice;
        set
        {
            var version = ++noticeVersion;
            if (Set(ref notice, value)) Notify(nameof(HasNotice));
            if (value is not null) _ = ExpireNoticeAsync(version);
        }
    }

    public bool HasNotice => notice is not null;

    /// <summary>Whether Gecko's window is attached to Mosaic's; kept up to date by the window.</summary>
    public bool IsGeckoAttached { get => isGeckoAttached; set => Set(ref isGeckoAttached, value); }

    // The toolbar reads the selected tab's state through the model.
    public bool CanGoBack { get => canGoBack; private set => Set(ref canGoBack, value); }
    public bool CanGoForward { get => canGoForward; private set => Set(ref canGoForward, value); }
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public bool CanReload { get => canReload; private set => Set(ref canReload, value); }

    public ContentKind Content
    {
        get => content;
        private set
        {
            if (!Set(ref content, value)) return;
            Notify(nameof(ShowsStartPage));
            Notify(nameof(ShowsGeckoPage));
        }
    }

    public bool ShowsStartPage => content == ContentKind.StartPage;

    /// <summary>
    /// True when the selected tab is a live Gecko page, i.e. the content area belongs to Gecko's
    /// window rather than to one of Mosaic's own views.
    /// </summary>
    public bool ShowsGeckoPage => content == ContentKind.GeckoPage;

    /// <summary>What to say in the content area when neither a page nor the start page is there.</summary>
    public ContentMessage? Message
    {
        get => message;
        private set
        {
            if (Set(ref message, value)) Notify(nameof(ShowsMessage));
        }
    }

    public bool ShowsMessage => message is not null;

    /// <summary>Chrome Web Store extension ID if the selected tab is showing a listing.</summary>
    public string? WebStoreExtensionId
    {
        get => webStoreExtensionId;
        private set
        {
            if (Set(ref webStoreExtensionId, value)) Notify(nameof(ShowsAddExtension));
        }
    }

    public bool ShowsAddExtension => webStoreExtensionId is not null;

    public string AddExtensionLabel { get => addExtensionLabel; private set => Set(ref addExtensionLabel, value); }

    /// <summary>Put keyboard focus in the address field.</summary>
    public event Action? AddressFocusRequested;

    /// <summary>Bring Mosaic's window to the front. Never raised while <see cref="AllowsFocusChanges"/> is off.</summary>
    public event Action? WindowActivationRequested;

    /// <summary>Give keyboard focus to Gecko's window. Never raised while <see cref="AllowsFocusChanges"/> is off.</summary>
    public event Action? GeckoFocusRequested;

    // ---- Tabs ----

    public BrowserTab NewTab(string url = "", EngineKind? engine = null, bool select = true)
    {
        var tab = new BrowserTab(engine ?? this.engine);
        InsertAfterSelected(tab);
        if (select) Select(tab);
        if (url.Length == 0)
        {
            if (select) FocusAddressBar();
        }
        else
        {
            Load(url, tab);
        }
        return tab;
    }

    private void InsertAfterSelected(BrowserTab tab)
    {
        var index = selectedTab is null ? -1 : Tabs.IndexOf(selectedTab);
        if (index >= 0) Tabs.Insert(index + 1, tab);
        else Tabs.Add(tab);
    }

    public void Select(BrowserTab tab)
    {
        if (!Tabs.Contains(tab)) return;
        lastLocalSelection = time.GetUtcNow();
        SelectLocally(tab);
        if (tab is { Engine: EngineKind.Gecko, GeckoId: { } id }) Gecko.Send("selectTab", id);
    }

    public void Close(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        DetachFromEngine(tab);
        Tabs.RemoveAt(index);
        var wasSelected = ReferenceEquals(selectedTab, tab);
        if (wasSelected) SelectedTab = null;
        if (Tabs.Count == 0) NewTab();
        else if (wasSelected) Select(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    public void CloseSelectedTab()
    {
        if (selectedTab is { } tab) Close(tab);
    }

    public void SelectAdjacentTab(int offset)
    {
        if (selectedTab is null || Tabs.Count < 2) return;
        var index = Tabs.IndexOf(selectedTab);
        Select(Tabs[(index + offset + Tabs.Count) % Tabs.Count]);
    }

    private void TabChanged(BrowserTab tab)
    {
        LibraryTabChanged(tab);  // library: history from every tab, both engines (HistoryRecorder)
        if (!ReferenceEquals(tab, selectedTab)) return;
        if (!IsEditingAddress) AddressText = tab.Url;
        UpdatePresentation();
    }

    // ---- Navigation ----

    /// <summary>Handles whatever was submitted in the address bar or the start page.</summary>
    public void Navigate(string input)
    {
        input = LibraryResolveKeyword(input);  // library: search keywords ("w swift" → Wikipedia)
        if (rules.ResolveAddress(input, searchEngine) is not { } url) return;
        var tab = selectedTab ?? NewTab();
        IsEditingAddress = false;
        Load(url, tab);
        LibraryNoteTyped(tab);  // library: typed visits rank higher
        FocusContent();
    }

    public void Load(string url, BrowserTab tab)
    {
        tab.Url = url;
        tab.Title = "";
        tab.Failure = null;
        tab.IsLoading = true;
        tab.LastLoadRequest = time.GetUtcNow();
        TabChanged(tab);

        switch (tab.Engine)
        {
            case EngineKind.Chromium when !chromium.IsAvailable:
                tab.IsLoading = false;
                tab.Failure = ChromiumUnavailableReason;
                break;
            case EngineKind.Chromium when tab.Chromium is { } browser:
                browser.Load(url);
                break;
            case EngineKind.Chromium:
                AttachChromium(tab, chromium.CreateBrowser(url));
                break;
            default:
                tab.GeckoWork = LoadInGeckoAsync(url, tab, tab.GeckoWork, tab.GeckoGeneration);
                break;
        }
        UpdatePresentation();
    }

    /// <summary>
    /// Opens the tab in Gecko, or navigates it if Gecko already has it. Each tab's requests run
    /// one after another (<paramref name="previous"/>), so two quick loads cannot both find the
    /// tab missing from Gecko and create it twice.
    /// </summary>
    private async Task LoadInGeckoAsync(string url, BrowserTab tab, Task? previous, int generation)
    {
        if (previous is not null) await previous;

        bool StillWanted() => !isShutDown && Tabs.Contains(tab) && tab.Engine == EngineKind.Gecko && tab.GeckoGeneration == generation;

        try
        {
            await Gecko.StartAsync();
            // The tab may have been closed, moved to Chromium, or sent somewhere else while Gecko
            // was starting.
            if (!StillWanted() || tab.Url != url) return;

            if (tab.GeckoId is { } id)
            {
                await Gecko.CallAsync("navigate", id, url);
            }
            else
            {
                JsonNode? result;
                pendingGeckoOpens++;
                try
                {
                    result = await Gecko.CallAsync("openTab", url, ReferenceEquals(tab, selectedTab));
                }
                finally
                {
                    pendingGeckoOpens--;
                }

                var opened = result is JsonValue value && value.TryGetValue(out int number) ? number : (int?)null;
                if (!StillWanted())
                {
                    if (opened is { } orphan)
                    {
                        closedGeckoIds.Add(orphan);
                        Gecko.Send("closeTab", orphan);
                    }
                    return;
                }
                tab.GeckoId = opened ?? throw new InvalidOperationException("Gecko did not open the tab.");
            }
            UpdatePresentation();
        }
        catch (Exception error)
        {
            // Whatever went wrong (no runtime, start timed out, Gecko quit mid-call), the tab
            // shows the reason and Reload tries again.
            if (!StillWanted()) return;
            tab.IsLoading = false;
            tab.Failure = error.Message;
            TabChanged(tab);
        }
    }

    public void GoBack()
    {
        if (selectedTab is not { } tab) return;
        if (tab.Engine == EngineKind.Chromium) tab.Chromium?.GoBack();
        else if (tab.GeckoId is { } id) Gecko.Send("goBack", id);
    }

    public void GoForward()
    {
        if (selectedTab is not { } tab) return;
        if (tab.Engine == EngineKind.Chromium) tab.Chromium?.GoForward();
        else if (tab.GeckoId is { } id) Gecko.Send("goForward", id);
    }

    public void Reload()
    {
        if (selectedTab is not { IsStartPage: false } tab) return;
        if (tab.Engine == EngineKind.Chromium)
        {
            if (tab.Chromium is { } browser) browser.Reload();
            else Load(tab.Url, tab);
        }
        else if (tab.GeckoId is { } id && Gecko.Status == GeckoStatus.Running)
        {
            Gecko.Send("reload", id);
        }
        else
        {
            Load(tab.Url, tab);
        }
    }

    public void StopLoading()
    {
        if (selectedTab is not { } tab) return;
        if (tab.Engine == EngineKind.Chromium) tab.Chromium?.Stop();
        else if (tab.GeckoId is { } id) Gecko.Send("stop", id);
    }

    /// <summary>The toolbar's one button that is Stop while a page loads and Reload otherwise.</summary>
    public void ReloadOrStop()
    {
        if (isLoading) StopLoading();
        else Reload();
    }

    /// <param name="activating">Pulls Mosaic to the front first; needed when the request comes
    /// from inside the Gecko window (Ctrl+L pressed there).</param>
    public void FocusAddressBar(bool activating = false)
    {
        if (activating) RequestWindowActivation();
        AddressFocusRequested?.Invoke();
    }

    private void RequestWindowActivation()
    {
        if (AllowsFocusChanges) WindowActivationRequested?.Invoke();
    }

    /// <summary>The address field took keyboard focus.</summary>
    public void BeginAddressEditing() => IsEditingAddress = true;

    /// <summary>The address field lost keyboard focus: whatever was typed and not submitted is dropped.</summary>
    public void EndAddressEditing()
    {
        IsEditingAddress = false;
        if (selectedTab is { } tab) AddressText = tab.Url;
    }

    /// <summary>
    /// Hands keyboard focus to the page of the selected tab. Focusing a page means focusing a
    /// native window, which can activate Mosaic, so this does nothing while
    /// <see cref="AllowsFocusChanges"/> is off.
    /// </summary>
    public void FocusContent()
    {
        if (!AllowsFocusChanges || selectedTab is not { IsStartPage: false } tab) return;
        if (tab.Engine == EngineKind.Chromium) tab.Chromium?.Focus();
        else _ = FocusGeckoSoonAsync(tab);
    }

    private async Task FocusGeckoSoonAsync(BrowserTab tab)
    {
        // Give Gecko a beat to exist and be shown before it takes focus.
        await Task.Delay(FocusDelay, time);
        if (!AllowsFocusChanges || !ReferenceEquals(selectedTab, tab) || tab.Engine != EngineKind.Gecko
            || tab.GeckoId is null || IsEditingAddress)
        {
            return;
        }
        Gecko.Send("focusContent");
        GeckoFocusRequested?.Invoke();
    }

    public void Execute(BrowserCommand command)
    {
        switch (command)
        {
            case BrowserCommand.NewTab: NewTab(); break;
            case BrowserCommand.CloseTab: CloseSelectedTab(); break;
            case BrowserCommand.FocusAddress: FocusAddressBar(); break;
            case BrowserCommand.Reload: Reload(); break;
            case BrowserCommand.Back: GoBack(); break;
            case BrowserCommand.Forward: GoForward(); break;
            case BrowserCommand.NextTab: SelectAdjacentTab(1); break;
            case BrowserCommand.PreviousTab: SelectAdjacentTab(-1); break;
            case BrowserCommand.ToggleEngine: ToggleEngine(); break;
            case BrowserCommand.NextSearchEngine: CycleSearchEngine(); break;
            case BrowserCommand.ShowExtensions: ShowExtensions = true; break;
            case BrowserCommand.ToggleBlocker: ToggleBlocker(); break;
        }
    }

    // ---- Engine switching ----

    /// <summary>
    /// Moves the selected tab to <paramref name="kind"/> (reloading its page there) and makes
    /// <paramref name="kind"/> the engine for new tabs.
    /// </summary>
    public void SetEngine(EngineKind kind)
    {
        if (kind == EngineKind.Chromium && !chromium.IsAvailable)
        {
            Notice = ChromiumUnavailableReason;
            return;
        }
        SetEnginePreference(kind, remember: true);
        if (selectedTab is not { } tab || tab.Engine == kind) return;

        var url = tab.Url;
        DetachFromEngine(tab);
        tab.Engine = kind;
        tab.CanGoBack = false;
        tab.CanGoForward = false;
        tab.IsLoading = false;
        tab.Failure = null;
        tab.BlockedCount = 0;
        if (url.Length > 0) Load(url, tab);
        TabChanged(tab);
    }

    public void ToggleEngine() => SetEngine((selectedTab?.Engine ?? engine).Other());

    /// <summary>
    /// <see cref="Engine"/> follows the selected tab so a new tab opens in its neighbour's engine.
    /// Only an explicit switch (<paramref name="remember"/>) changes what Mosaic starts with next time.
    /// </summary>
    private void SetEnginePreference(EngineKind kind, bool remember = false)
    {
        Engine = kind;
        if (!remember) return;
        settings.Engine = kind;
        settings.Save();
    }

    public void CycleSearchEngine()
    {
        SearchEngine = rules.NextSearchEngine(searchEngine);
        Notice = $"Search engine: {searchEngine.Name}";
    }

    private string ChromiumUnavailableReason =>
        chromium.UnavailableReason ?? "The Chromium engine is not available on this PC.";

    /// <summary>Releases whatever the tab holds in its current engine.</summary>
    private void DetachFromEngine(BrowserTab tab)
    {
        if (tab.Chromium is { } browser)
        {
            tab.Chromium = null;
            browser.Close();
        }
        tab.GeckoGeneration++;
        tab.GeckoWork = null;
        if (tab.GeckoId is { } id)
        {
            tab.GeckoId = null;
            closedGeckoIds.Add(id);
            Gecko.Send("closeTab", id);
        }
    }

    // ---- Chromium mirroring ----

    private void AttachChromium(BrowserTab tab, IChromiumBrowser browser)
    {
        // A browser the tab has let go of may still have events in flight; they are not the tab's.
        bool Current() => ReferenceEquals(tab.Chromium, browser);

        browser.TitleChanged += title =>
        {
            if (!Current()) return;
            tab.Title = title;
            TabChanged(tab);
        };
        browser.UrlChanged += url =>
        {
            if (!Current() || IsSettling(tab, url, tab.IsLoading)) return;
            tab.Url = url;
            TabChanged(tab);
        };
        browser.LoadStateChanged += state =>
        {
            if (!Current()) return;
            tab.IsLoading = state.IsLoading;
            tab.CanGoBack = state.CanGoBack;
            tab.CanGoForward = state.CanGoForward;
            TabChanged(tab);
        };
        browser.NewTabRequested += url =>
        {
            if (Current()) NewTab(url, tab.Engine);
        };
        browser.BlockedCountChanged += count =>
        {
            if (!Current()) return;
            tab.BlockedCount = count;
            TabChanged(tab);
        };
        browser.Failed += reason =>
        {
            if (!Current()) return;
            tab.Chromium = null;
            browser.Close();
            tab.IsLoading = false;
            tab.Failure = reason;
            TabChanged(tab);
            UpdatePresentation();
        };
        tab.Chromium = browser;
    }

    // ---- Gecko mirroring ----

    /// <summary>
    /// Brings Mosaic's tab list in line with what Gecko reports: page-driven navigations, tabs
    /// Gecko opened itself (target=_blank, extension pages), and tabs the page closed.
    /// </summary>
    private void Reconcile(GeckoSnapshot snapshot)
    {
        if (isShutDown) return;

        var byGeckoId = new Dictionary<int, BrowserTab>();
        foreach (var tab in Tabs)
        {
            if (tab is { Engine: EngineKind.Gecko, GeckoId: { } id }) byGeckoId[id] = tab;
        }
        var reported = snapshot.Tabs.Select(state => state.Id).ToHashSet();
        var geckoSelection = snapshot.Tabs.FirstOrDefault(state => state.Selected)?.Id;

        try
        {
            foreach (var state in snapshot.Tabs)
            {
                if (closedGeckoIds.Contains(state.Id)) continue;
                if (byGeckoId.TryGetValue(state.Id, out var known))
                {
                    Apply(state, known);
                }
                else if (pendingGeckoOpens == 0)
                {
                    // While one of Mosaic's own opens is in flight an unknown tab is most likely
                    // that one, about to be claimed; adopting it would show it twice.
                    var adopted = new BrowserTab(EngineKind.Gecko) { GeckoId = state.Id };
                    Apply(state, adopted);
                    // Show it if the user is on a Gecko page (they clicked a link that opens a
                    // tab) or asked for it; a tab Gecko opens on its own, such as an extension's
                    // welcome page, stays in the background.
                    var onGeckoPage = selectedTab is { Engine: EngineKind.Gecko, GeckoId: not null };
                    InsertAfterSelected(adopted);
                    byGeckoId[state.Id] = adopted;
                    var requested = selectWhenAdopted.Remove(state.Id);
                    if (requested || (state.Selected && onGeckoPage)) SelectLocally(adopted);
                }
            }

            if (pendingGeckoOpens == 0)
            {
                foreach (var (id, tab) in byGeckoId)
                {
                    if (reported.Contains(id)) continue;
                    // Gecko already closed it; there is nothing to send back.
                    tab.GeckoId = null;
                    Close(tab);
                }
            }

            // Follow a tab switch made inside Gecko (a page focusing its opener, an extension
            // activating a tab). Only a *change* in Gecko's selection counts, and only while a
            // live Gecko tab is showing here; otherwise a blank or Chromium tab the user just
            // opened would be yanked away.
            if (geckoSelection is { } current && lastGeckoSelection is { } last && current != last
                && time.GetUtcNow() - lastLocalSelection > SelectionGrace
                && selectedTab is { Engine: EngineKind.Gecko, GeckoId: not null } showing
                && byGeckoId.TryGetValue(current, out var selectedInGecko)
                && !ReferenceEquals(selectedInGecko, showing))
            {
                SelectLocally(selectedInGecko);
            }
        }
        finally
        {
            lastGeckoSelection = geckoSelection;
        }

        foreach (var type in snapshot.Events) Handle(type);
        UpdatePresentation();
    }

    private void Apply(GeckoTabState state, BrowserTab tab)
    {
        var settling = IsSettling(tab, state.Url, state.Loading);
        if (!settling)
        {
            tab.Url = state.Url;
            tab.Title = state.Title;
        }
        if (!settling || state.Loading) tab.IsLoading = state.Loading;
        tab.CanGoBack = state.CanGoBack;
        tab.CanGoForward = state.CanGoForward;
        tab.BlockedCount = state.Blocked;
        TabChanged(tab);
    }

    /// <summary>
    /// Just after a load is requested the engine still reports the blank page it started from;
    /// that must not flash into the address bar.
    /// </summary>
    private bool IsSettling(BrowserTab tab, string reportedUrl, bool reportedLoading) =>
        reportedUrl == BlankPage && tab.Url.Length > 0
        && (reportedLoading || time.GetUtcNow() - tab.LastLoadRequest < SettlingTime);

    /// <summary>Selects a tab without telling Gecko, which is right when Gecko already made it current.</summary>
    private void SelectLocally(BrowserTab tab)
    {
        SelectedTab = tab;
        SetEnginePreference(tab.Engine);
        IsEditingAddress = false;
        AddressText = tab.Url;
        UpdatePresentation();
    }

    /// <summary>Keyboard shortcuts pressed inside Gecko arrive here as snapshot events.</summary>
    private void Handle(string eventType)
    {
        if (LibraryHandleGeckoEvent(eventType)) return;  // library: Ctrl+D, Ctrl+H, Ctrl+Shift+O/B pressed in a Gecko page
        switch (eventType)
        {
            case "focusAddress":
                FocusAddressBar(activating: true);
                break;
            case "newTab":
                RequestWindowActivation();
                NewTab();
                break;
            case "closeTab":
                CloseSelectedTab();
                break;
            case "toggleEngine":
                ToggleEngine();
                break;
            case "toggleBlocker":
                ToggleBlocker();
                break;
        }
    }

    private void GeckoStopped()
    {
        if (isShutDown) return;
        closedGeckoIds.Clear();
        selectWhenAdopted.Clear();
        lastGeckoSelection = null;
        foreach (var tab in Tabs)
        {
            if (tab.Engine != EngineKind.Gecko) continue;
            tab.GeckoId = null;
            tab.GeckoGeneration++;
            tab.GeckoWork = null;
            tab.IsLoading = false;
            if (!tab.IsStartPage) tab.Failure = "Gecko stopped. Reload to start it again.";
        }
        UpdatePresentation();
    }

    private void GeckoStatusChanged()
    {
        if (isShutDown) return;
        // The "starting" message and the Gecko page itself both depend on the engine's status.
        UpdatePresentation();
    }

    // ---- Presentation ----

    /// <summary>
    /// Recomputes everything the window derives from the selected tab and the engines: toolbar
    /// state, what the content area shows, and which Chromium browser (if any) is visible.
    /// </summary>
    private void UpdatePresentation()
    {
        var tab = selectedTab;
        CanGoBack = tab?.CanGoBack ?? false;
        CanGoForward = tab?.CanGoForward ?? false;
        IsLoading = tab?.IsLoading ?? false;
        CanReload = tab is { IsStartPage: false };

        var kind = ContentFor(tab);
        Message = MessageFor(kind, tab);
        Content = kind;

        foreach (var other in Tabs)
        {
            if (other.Chromium is { } browser)
            {
                browser.IsShown = ReferenceEquals(other, tab) && kind == ContentKind.ChromiumPage;
            }
        }

        var listing = tab is not null && tab.Url.Contains("/detail/", StringComparison.Ordinal)
            ? rules.WebStoreExtensionId(tab.Url)
            : null;
        WebStoreExtensionId = listing;
        AddExtensionLabel = listing is not null && Extensions.IsInstalled(listing) ? "Reinstall" : "Add to Mosaic";

        UpdateBlockerPresentation(tab);
    }

    /// <summary>The shield and the popover, as <c>BlockerButton</c> and <c>BlockerPopover</c> derive them on macOS.</summary>
    private void UpdateBlockerPresentation(BrowserTab? tab)
    {
        var filtering = tab is not null && Blocker.IsFiltering(tab.Url);
        var count = tab?.BlockedCount ?? 0;
        var countText = count == 1 ? "1 request blocked on this page" : $"{count} requests blocked on this page";

        IsFilteringPage = filtering;
        BlockedCountText = countText;
        BlockerSite = tab is null ? null : ContentBlocker.SiteOfPage(tab.Url);
        IsBlockingSite = blockerSite is not { } site || !Blocker.Settings.IsAllowlisted(site);
        BlockerShield = !Blocker.IsAvailable || !Blocker.IsEnabled ? BlockerShield.Off
            : filtering ? BlockerShield.Filtering : BlockerShield.Idle;
        BlockerBadge = filtering && count > 0 ? (count > 999 ? "999+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture)) : "";
        BlockerHelp =
            !Blocker.IsAvailable ? "Content blocker is not included in this build"
            : !Blocker.IsEnabled ? "Content blocker is off"
            : !filtering ? "Content blocker is not active on this page"
            : countText;
    }

    private ContentKind ContentFor(BrowserTab? tab)
    {
        if (tab is null || tab.IsStartPage) return ContentKind.StartPage;
        if (tab.Failure is not null) return ContentKind.Failure;
        if (tab.Engine == EngineKind.Chromium)
        {
            return tab.Chromium is null ? ContentKind.ChromiumStarting : ContentKind.ChromiumPage;
        }
        if (Gecko.Status != GeckoStatus.Running) return ContentKind.GeckoStarting;
        return tab.GeckoId is null ? ContentKind.GeckoOpening : ContentKind.GeckoPage;
    }

    private ContentMessage? MessageFor(ContentKind kind, BrowserTab? tab)
    {
        var runtime = Gecko.Runtime is { } found ? $"{found.Name} {found.Version}" : null;
        return kind switch
        {
            ContentKind.Failure => new("This page can’t be shown", tab?.Failure, ShowsProgress: false, IsFailure: true),
            ContentKind.ChromiumStarting => new("Starting Chromium…", null, ShowsProgress: false, IsFailure: false),
            ContentKind.GeckoStarting => new("Starting Mozilla engine…", runtime, ShowsProgress: true, IsFailure: false),
            ContentKind.GeckoOpening => new("Opening page…", runtime, ShowsProgress: true, IsFailure: false),
            _ => null,
        };
    }

    private void UpdateAddressKind()
    {
        if (addressText.Length == 0 || !rules.IsAddress(addressText)) AddressKind = AddressKind.Search;
        else AddressKind = addressText.StartsWith("https://", StringComparison.Ordinal) ? AddressKind.Secure : AddressKind.Web;
    }

    private async Task ExpireNoticeAsync(int version)
    {
        await Task.Delay(NoticeDuration, time);
        // A newer notice has its own timer.
        if (version == noticeVersion) Notice = null;
    }

    // ---- Content blocker ----

    /// <summary>Master switch. The page in front is reloaded so the change is visible.</summary>
    public void SetBlockerEnabled(bool enabled)
    {
        if (Blocker.IsEnabled == enabled) return;
        Blocker.SetEnabled(enabled);
        Notice = enabled ? "Content blocker on" : "Content blocker off";
        Reload();
    }

    public void ToggleBlocker() => SetBlockerEnabled(!Blocker.IsEnabled);

    /// <summary>Per-site switch for the site of <paramref name="tab"/>'s page; reloads it if it is in front.</summary>
    public void SetBlocking(bool blocking, BrowserTab tab)
    {
        Blocker.SetBlocking(blocking, tab.Url);
        if (ReferenceEquals(tab, selectedTab)) Reload();
    }

    // ---- Extensions ----

    /// <summary>Installs the extension whose Chrome Web Store listing the selected tab is showing.</summary>
    public async Task AddWebStoreExtensionAsync()
    {
        if (webStoreExtensionId is not { } id || Extensions.IsBusy) return;
        var installed = await Extensions.InstallFromWebStoreAsync(id);
        Notice = installed ? "Extension added to Mosaic." : Extensions.ErrorMessage;
    }

    /// <summary>Opens an extension's popup or options page (<paramref name="kind"/>) in a Gecko tab.</summary>
    public async Task OpenExtensionPageAsync(InstalledExtension extension, string kind)
    {
        try
        {
            await Gecko.StartAsync();
            await Extensions.EnsureLoadedAsync(extension);
            // The agent opens the page in a new Gecko tab; `Reconcile` adopts it.
            var result = await Gecko.CallAsync("openExtensionPage", extension.GeckoId, kind);
            if (result is not JsonValue value || !value.TryGetValue(out int id))
            {
                Notice = $"{extension.Name} has no {kind} page.";
                return;
            }
            ShowExtensions = false;
            if (Tabs.FirstOrDefault(tab => tab.GeckoId == id) is { } existing) SelectLocally(existing);
            else selectWhenAdopted.Add(id);
        }
        catch (Exception error)
        {
            // Gecko could not start or dropped the call; either way the message is presentable.
            Notice = error.Message;
        }
    }

    /// <summary>Closes every Chromium browser and stops Gecko. Called once, as the window closes.</summary>
    public void Shutdown()
    {
        if (isShutDown) return;
        isShutDown = true;
        LibraryShutdown();  // library: record visits still waiting for their title
        foreach (var tab in Tabs)
        {
            if (tab.Chromium is not { } browser) continue;
            tab.Chromium = null;
            browser.Close();
        }
        Gecko.StopAndWait(TimeSpan.FromSeconds(3));
        Blocker.Shutdown();
    }
}
