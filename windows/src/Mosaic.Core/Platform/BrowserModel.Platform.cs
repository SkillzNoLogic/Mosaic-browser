using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mosaic.Core.Extensions;
using Mosaic.Core.Gecko;
using Mosaic.Core.Platform;

namespace Mosaic.Core.Browser;

/// <summary>
/// The platform area's part of the browser model: the Gecko runtime's availability, the default
/// engine, per-site engine rules, safe engine switching, web-content languages and extension
/// toolbar buttons. Port of <c>Sources/Mosaic/Features/Platform/BrowserModel+Platform.swift</c> and
/// <c>SafeEngineSwitch.swift</c>. The hooks into <c>BrowserModel.cs</c> are one line each, marked
/// "platform": the constructor (<see cref="PlatformAttach"/>), <c>NewTab</c> and <c>Navigate</c>
/// (site rules), and <c>SetEngine</c> (safe switching).
/// </summary>
public sealed partial class BrowserModel
{
    private PlatformServices? platform;
    private bool geckoRuntimeMissing;

    /// <summary>The platform area's services (installer, rules, languages, updates, engine switches).</summary>
    public PlatformServices Platform => platform ??= new PlatformServices(null);

    /// <summary>Hook from the constructor, before the first tab: may pick Chromium when Gecko is missing.</summary>
    private void PlatformAttach()
    {
        var services = platform ??= new PlatformServices(PlatformDataDirectory(settings));
        services.RuntimeHost = () => PlatformRuntimeHost;
        services.LanguagesChanged += () =>
        {
            if (services.IsActive) _ = ApplyLanguagesToGeckoAsync();
        };
        geckoRuntimeMissing = Gecko.Runtime is null;

        // Languages go into Gecko each time it starts, after the blocker and extensions.
        var previous = Gecko.OnStarted;
        Gecko.OnStarted = async () =>
        {
            if (previous is not null) await previous();
            if (services.IsActive) await ApplyLanguagesToGeckoAsync();
        };

        // A PC without any Gecko runtime (the downloaded app on first launch): start in Chromium
        // so browsing works at once, unless the person already chose an engine. Mozilla tabs and
        // Settings › Engine offer to install the runtime.
        if (geckoRuntimeMissing && chromium.IsAvailable && settings.Engine is null) SetEnginePreference(EngineKind.Chromium);
    }

    /// <summary>
    /// Called once by the app as it starts (after the window exists): turns on what a bare model
    /// leaves off so other areas' tests keep describing the browser without the platform area:
    /// safe engine switching, giving the language list to Gecko, the automation commands and the
    /// <c>platform</c> dump field, and the update schedule (whose first check waits 30 seconds).
    /// </summary>
    public void ActivatePlatform()
    {
        var services = Platform;
        if (services.IsActive) return;
        services.IsActive = true;
        services.SafeEngineSwitching = true;
        PlatformAutomation.Register();
        services.Updates.StartSchedule();
        _ = ApplyLanguagesToGeckoAsync();
    }

    /// <summary>The folder of the model's <c>settings.json</c>; platform data lives beside it.</summary>
    private static string? PlatformDataDirectory(BrowserSettings settings)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(SettingsFilePath(settings)));
        }
        catch (Exception error) when (error is MissingFieldException or ArgumentException or NotSupportedException)
        {
            // Without knowing where the settings live, keep platform preferences in memory rather
            // than guess a folder (tests must never touch the real profile).
            return null;
        }
    }

    // BrowserSettings belongs to the winui area and does not expose its path; read the field
    // without reflection (trimming-safe). See the platform report: a public property would be nicer.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "path")]
    private static extern ref string SettingsFilePath(BrowserSettings settings);

    // ---- Engines ----

    /// <summary>
    /// True when no Gecko runtime can be found at all: Mozilla tabs cannot load, and the UI offers
    /// to install one (Settings › Engine, and the notice over a Mozilla tab).
    /// </summary>
    public bool GeckoRuntimeMissing => geckoRuntimeMissing;

    /// <summary>Whether a tab could load in <paramref name="kind"/> right now (or after starting the engine).</summary>
    public bool IsEngineUsable(EngineKind kind) => kind == EngineKind.Chromium ? chromium.IsAvailable : !geckoRuntimeMissing;

    /// <summary>The engine Mosaic starts with and new tabs use (until a tab of the other engine is selected). Persisted.</summary>
    public EngineKind DefaultEngine => settings.Engine ?? engine;

    public void SetDefaultEngine(EngineKind kind)
    {
        if (kind == EngineKind.Chromium && !chromium.IsAvailable) return;
        SetEnginePreference(kind, remember: true);
        // A blank new tab has no engine attached yet; let it follow the choice.
        if (selectedTab is { IsStartPage: true, Chromium: null, GeckoId: null } tab && tab.Engine != kind)
        {
            tab.Engine = kind;
            UpdatePresentation();
        }
        Notify(nameof(DefaultEngine));
    }

    /// <summary>
    /// The runtime was installed, updated or removed: look again, and reopen Mozilla tabs that are
    /// waiting for it (they lost their page, or never had one), without relaunching Mosaic.
    /// </summary>
    internal void PlatformEngineChanged()
    {
        var wasMissing = geckoRuntimeMissing;
        geckoRuntimeMissing = Platform.Runtime.InstalledRuntime is null
            && !(Gecko.Runtime is { IsManaged: false } borrowed && File.Exists(borrowed.ExecutablePath));
        if (wasMissing != geckoRuntimeMissing) Notify(nameof(GeckoRuntimeMissing));
        UpdatePresentation();
        if (geckoRuntimeMissing) return;
        foreach (var tab in Tabs.ToList())
        {
            if (tab is { Engine: EngineKind.Gecko, GeckoId: null, IsStartPage: false } && tab.Failure is not null) Load(tab.Url, tab);
        }
    }

    /// <summary>Quits Gecko for an install or removal and waits until it has stopped.</summary>
    internal async Task PlatformStopGeckoAsync()
    {
        Gecko.Stop();
        for (var waited = 0; waited < 100 && Gecko.Status is GeckoStatus.Starting or GeckoStatus.Running; waited++)
        {
            await Task.Delay(100);
        }
        if (Gecko.Status is GeckoStatus.Starting or GeckoStatus.Running)
        {
            throw new RuntimeInstallException(new RuntimeInstallFailure.EngineBusy());
        }
    }

    /// <summary>The installer's view of this model.</summary>
    private GeckoRuntimeHost PlatformRuntimeHost => new()
    {
        IsEngineRunning = () => Gecko.Status is GeckoStatus.Starting or GeckoStatus.Running,
        StopEngineAsync = PlatformStopGeckoAsync,
        EngineChanged = PlatformEngineChanged,
    };


    // ---- Languages ----

    /// <summary>Gives the current language list to Gecko (each start, and whenever it changes).</summary>
    public async Task ApplyLanguagesToGeckoAsync()
    {
        if (Gecko.Status != GeckoStatus.Running) return;
        var languages = Platform.Languages;
        try
        {
            await Gecko.CallAsync("setAcceptLanguages", languages.Preferences.PreferenceValue);
            languages.GeckoError = null;
        }
        catch (Exception error)
        {
            languages.GeckoError = error.Message;
        }
    }

    // ---- Per-site engine rules ----
    //
    // Rules apply when a page is opened from the address bar, the start page, or as a new tab with
    // an address (bookmarks, history, links opened by Mosaic itself). They do not apply to
    // navigations inside a page (following a link, redirects) or to tabs a page opens: moving a page
    // between engines mid-visit would reload it and lose its state.

    /// <summary>Hook from <c>NewTab</c>: the engine a rule names for a new tab opened at <paramref name="url"/>.</summary>
    private EngineKind? PlatformEngineForNewTab(string url)
    {
        if (url.Length == 0 || platform is null) return null;
        return platform.Rules.EngineFor(url) is { } kind && IsEngineUsable(kind) ? kind : null;
    }

    /// <summary>Hook from <c>Navigate</c>: moves <paramref name="tab"/> to the engine a rule names, before <paramref name="url"/> loads.</summary>
    private void PlatformApplyEngineRule(string url, BrowserTab tab)
    {
        if (platform?.Rules.RuleFor(url) is not { } rule || EngineKindExtensions.FromId(rule.Engine) is not { } kind
            || kind == tab.Engine || !IsEngineUsable(kind))
        {
            return;
        }
        CancelEngineSwitch(tab);
        DetachFromEngine(tab);
        tab.Engine = kind;
        tab.CanGoBack = false;
        tab.CanGoForward = false;
        tab.BlockedCount = 0;
        tab.Failure = null;
        if (ReferenceEquals(tab, selectedTab)) SetEnginePreference(kind);
        Notice = $"Opening in {kind.DisplayName()}: site rule for {rule.Domain}";
    }

    /// <summary>The site of the selected tab, as a rule would store it ("example.com"); null for anything but a web page.</summary>
    public string? CurrentSiteDomain =>
        selectedTab is { } tab && Uri.TryCreate(tab.Url, UriKind.Absolute, out var url)
        && url.Scheme is "http" or "https" ? EngineRuleSet.NormalizedHost(url.IdnHost) : null;

    /// <summary>"Always open this site in …", from the current tab. Moves the tab too.</summary>
    public bool AddEngineRuleForCurrentSite(EngineKind kind)
    {
        if (CurrentSiteDomain is not { } domain || !Platform.Rules.Set(domain, kind)) return false;
        Notice = $"{domain} will always open in {kind.DisplayName()}";
        if (selectedTab?.Engine != kind) SetEngine(kind);
        return true;
    }

    // ---- Extension toolbar buttons ----

    /// <summary>
    /// One toolbar button per enabled extension, in the order Settings › Extensions lists them. A
    /// click opens the extension's popup as a Mozilla tab (as on macOS), else its options, else
    /// Settings › Extensions (<see cref="ExtensionToolbarItem.OpensSettings"/>).
    /// </summary>
    public IReadOnlyList<ExtensionToolbarItem> ExtensionToolbarItems =>
        [.. Extensions.Extensions.Where(extension => extension.Enabled).Select(extension => new ExtensionToolbarItem(
            extension, Extensions.IsLoaded(extension), Extensions.Items.FirstOrDefault(item => item.Extension.Id == extension.Id)?.IconFile))];

    /// <summary>Performs a toolbar button: popup, options, or (returns false) nothing to open here.</summary>
    public bool PerformExtensionToolbarItem(ExtensionToolbarItem item)
    {
        if (item.Extension.HasPopup) _ = OpenExtensionPageAsync(item.Extension, "popup");
        else if (item.Extension.HasOptions) _ = OpenExtensionPageAsync(item.Extension, "options");
        else return false;
        return true;
    }

    // ---- Safe engine switching ----
    //
    // Moving a tab to the other engine keeps the original page on screen until the other engine
    // has loaded it. If the other engine fails (network or certificate error, not installed, too
    // slow, its process died), the tab stays where it was and a notice says why.
    //
    // Chromium → Mozilla: Gecko opens the address in a background tab Mosaic does not mirror; the
    // Chromium page stays until the agent's pageStatus reports the page loaded.
    // Mozilla → Chromium: a Chromium browser loads the address hidden; the Gecko page stays until
    // that browser reports the load finished.

    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(30);
    /// <summary>A page that shows its document but keeps loading (slow trackers, streaming) is taken over after this long.</summary>
    private static readonly TimeSpan CommitGrace = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan GeckoPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ChromiumPollInterval = TimeSpan.FromMilliseconds(150);

    private enum SwitchResult { Committed, Failed, Abandoned }

    /// <summary>
    /// Hook from <c>SetEngine</c>. Returns false when there is nothing to keep on screen (safe
    /// switching off, a blank tab, Mosaic's own page, a failed page, or a page not loaded yet): the
    /// caller then switches directly.
    /// </summary>
    private bool PlatformBeginSafeSwitch(EngineKind kind)
    {
        if (platform is not { SafeEngineSwitching: true } services || selectedTab is not { } tab) return false;
        if (tab.Engine == kind)
        {
            CancelEngineSwitch(tab);
            return false;
        }
        if (services.Switches.TargetOf(tab) == kind) return true;
        var isLive = tab.Engine == EngineKind.Chromium ? tab.Chromium is not null : tab.GeckoId is not null && Gecko.Status == GeckoStatus.Running;
        if (!isLive || tab.IsStartPage || tab.Failure is not null || tab.Url == BlankPage
            || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var parsed) || parsed.Scheme == "mosaic")
        {
            return false;
        }

        var address = tab.Url;
        var original = tab.Engine;
        var since = tab.LastLoadRequest;
        Notice = $"Opening this page in {kind.DisplayName()}…";
        var cancellation = new CancellationTokenSource();
        services.Switches.Begin(tab, new EngineSwitchTracker.Pending(kind, address, time.GetUtcNow(), cancellation));
        // The engine for new tabs follows the explicit choice, as before.
        SetEnginePreference(kind, remember: true);
        _ = RunSwitchAsync(tab, kind, address, original, since, cancellation.Token);
        return true;
    }

    /// <summary>Stops moving <paramref name="tab"/> to the other engine; the page stays where it is.</summary>
    public void CancelEngineSwitch(BrowserTab tab) => platform?.Switches.Cancel(tab);

    /// <summary>The engine <paramref name="tab"/> is being moved to, if a switch is under way.</summary>
    public EngineKind? EngineSwitchTarget(BrowserTab tab) => platform?.Switches.TargetOf(tab);

    private async Task RunSwitchAsync(BrowserTab tab, EngineKind kind, string address, EngineKind original, DateTimeOffset since, CancellationToken token)
    {
        (SwitchResult Result, string? Reason) outcome;
        try
        {
            outcome = kind == EngineKind.Gecko
                ? await SwitchToGeckoAsync(tab, address, original, since, token)
                : await SwitchToChromiumAsync(tab, address, original, since, token);
        }
        catch (Exception error)
        {
            outcome = (SwitchResult.Failed, error.Message);
        }
        FinishSwitch(tab, kind, original, outcome.Result, outcome.Reason);
    }

    /// <summary>The tab exists, is in the engine it started in, and has not gone elsewhere meanwhile.</summary>
    private bool SwitchStillWanted(BrowserTab tab, string address, EngineKind original, DateTimeOffset since, CancellationToken token) =>
        !token.IsCancellationRequested && !isShutDown && Tabs.Contains(tab) && tab.Engine == original
        && tab.LastLoadRequest == since && SameDocument(tab.Url, address);

    internal static bool SameDocument(string left, string right) => left.Split('#', 2)[0] == right.Split('#', 2)[0];

    private async Task<(SwitchResult, string?)> SwitchToGeckoAsync(
        BrowserTab tab, string address, EngineKind original, DateTimeOffset since, CancellationToken token)
    {
        if (geckoRuntimeMissing) return (SwitchResult.Failed, "The Mozilla engine is not installed. Install it in Settings › Engine.");
        int? geckoTab = null;
        try
        {
            await Gecko.StartAsync();
            if (!SwitchStillWanted(tab, address, original, since, token)) return (SwitchResult.Abandoned, null);
            pendingGeckoOpens++;
            try
            {
                var opened = await Gecko.CallAsync("openTab", address, false);
                if (opened is JsonValue value && value.TryGetValue(out int id))
                {
                    geckoTab = id;
                    // Out of Mosaic's tab list until the switch commits.
                    closedGeckoIds.Add(id);
                }
            }
            finally
            {
                pendingGeckoOpens--;
            }
            if (geckoTab is not { } hidden) return (SwitchResult.Failed, "Mozilla did not open a tab.");

            var started = time.GetUtcNow();
            DateTimeOffset? committedAt = null;
            while (true)
            {
                await Task.Delay(GeckoPollInterval, time, token);
                if (!SwitchStillWanted(tab, address, original, since, token))
                {
                    Gecko.Send("closeTab", hidden);
                    return (SwitchResult.Abandoned, null);
                }
                var status = await Gecko.CallAsync("pageStatus", hidden) as JsonObject ?? [];
                if (Text(status["error"]) is { } error)
                {
                    Gecko.Send("closeTab", hidden);
                    return (SwitchResult.Failed, $"Mozilla could not load the page ({error}).");
                }
                var document = Text(status["document"]) ?? "";
                var loading = status["loading"] is not JsonValue flag || !flag.TryGetValue(out bool busy) || busy;
                if (document.Length > 0 && document != BlankPage)
                {
                    committedAt ??= time.GetUtcNow();
                    if (!loading || time.GetUtcNow() - committedAt > CommitGrace)
                    {
                        return await CommitToGeckoAsync(tab, hidden, loading, Text(status["title"]) ?? "");
                    }
                }
                if (time.GetUtcNow() - started > SwitchTimeout)
                {
                    Gecko.Send("closeTab", hidden);
                    return (SwitchResult.Failed, $"Mozilla took longer than {SwitchTimeout.TotalSeconds:0} seconds to load the page.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (geckoTab is { } id) Gecko.Send("closeTab", id);
            return (SwitchResult.Abandoned, null);
        }
        catch (Exception error)
        {
            if (geckoTab is { } id) Gecko.Send("closeTab", id);
            return (SwitchResult.Failed, error.Message);
        }
    }

    private async Task<(SwitchResult, string?)> CommitToGeckoAsync(BrowserTab tab, int id, bool loading, string title)
    {
        if (ReferenceEquals(tab, selectedTab))
        {
            try { await Gecko.CallAsync("selectTab", id); } catch (Exception) { /* the snapshot corrects it */ }
        }
        if (!Tabs.Contains(tab) || tab.Engine != EngineKind.Chromium || isShutDown)
        {
            Gecko.Send("closeTab", id);
            return (SwitchResult.Abandoned, null);
        }
        var oldBrowser = tab.Chromium;
        closedGeckoIds.Remove(id);
        tab.Chromium = null;
        tab.GeckoGeneration++;
        tab.GeckoWork = null;
        tab.Engine = EngineKind.Gecko;
        tab.GeckoId = id;
        tab.IsLoading = loading;
        tab.CanGoBack = false;
        tab.CanGoForward = false;
        tab.BlockedCount = 0;
        if (title.Length > 0) tab.Title = title;
        if (ReferenceEquals(tab, selectedTab)) lastLocalSelection = time.GetUtcNow();
        TabChanged(tab);
        UpdatePresentation();
        // Gecko's window is shown first; the Chromium page goes a moment later, so the content
        // area never shows through to the window behind.
        if (oldBrowser is not null) _ = CloseLaterAsync(oldBrowser, TimeSpan.FromMilliseconds(300));
        return (SwitchResult.Committed, null);
    }

    private async Task CloseLaterAsync(IChromiumBrowser browser, TimeSpan delay)
    {
        try { await Task.Delay(delay, time); } catch (OperationCanceledException) { }
        browser.IsShown = false;
        browser.Close();
    }

    private async Task<(SwitchResult, string?)> SwitchToChromiumAsync(
        BrowserTab tab, string address, EngineKind original, DateTimeOffset since, CancellationToken token)
    {
        if (!chromium.IsAvailable) return (SwitchResult.Failed, ChromiumUnavailableReason);
        var browser = chromium.CreateBrowser(address);
        browser.IsShown = false;
        var probe = new ChromiumLoadProbe(browser, time);
        var started = time.GetUtcNow();
        try
        {
            while (true)
            {
                await Task.Delay(ChromiumPollInterval, time, token);
                if (!SwitchStillWanted(tab, address, original, since, token))
                {
                    probe.Detach();
                    browser.Close();
                    return (SwitchResult.Abandoned, null);
                }
                if (probe.Error is { } error)
                {
                    probe.Detach();
                    browser.Close();
                    return (SwitchResult.Failed, $"Chromium could not load the page ({error}).");
                }
                if (probe.CommittedAt is { } committed && (probe.Finished || time.GetUtcNow() - committed > CommitGrace))
                {
                    return (CommitToChromium(tab, browser, probe), null);
                }
                if (time.GetUtcNow() - started > SwitchTimeout)
                {
                    probe.Detach();
                    browser.Close();
                    return (SwitchResult.Failed, $"Chromium took longer than {SwitchTimeout.TotalSeconds:0} seconds to load the page.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            probe.Detach();
            browser.Close();
            return (SwitchResult.Abandoned, null);
        }
    }

    private SwitchResult CommitToChromium(BrowserTab tab, IChromiumBrowser browser, ChromiumLoadProbe probe)
    {
        probe.Detach();
        var geckoId = tab.GeckoId;
        tab.GeckoGeneration++;
        tab.GeckoWork = null;
        tab.GeckoId = null;
        // Hidden from mirroring now; closed once the Chromium page is in front.
        if (geckoId is { } hidden) closedGeckoIds.Add(hidden);
        tab.Engine = EngineKind.Chromium;
        AttachChromium(tab, browser);
        if (probe.Url is { Length: > 0 } url) tab.Url = url;
        if (probe.Title is { Length: > 0 } title) tab.Title = title;
        tab.IsLoading = !probe.Finished;
        tab.CanGoBack = probe.CanGoBack;
        tab.CanGoForward = probe.CanGoForward;
        tab.BlockedCount = probe.Blocked;
        tab.Failure = null;
        TabChanged(tab);
        UpdatePresentation();
        if (geckoId is { } id) _ = CloseGeckoTabLaterAsync(id);
        return SwitchResult.Committed;
    }

    private async Task CloseGeckoTabLaterAsync(int id)
    {
        try { await Task.Delay(TimeSpan.FromMilliseconds(200), time); } catch (OperationCanceledException) { }
        Gecko.Send("closeTab", id);
    }

    private void FinishSwitch(BrowserTab tab, EngineKind kind, EngineKind original, SwitchResult result, string? reason)
    {
        var switches = Platform.Switches;
        switch (result)
        {
            case SwitchResult.Committed:
                switches.End(tab, $"committed to {kind.Id()}");
                if (notice?.StartsWith("Opening this page in", StringComparison.Ordinal) == true) Notice = null;
                break;
            case SwitchResult.Failed:
                switches.End(tab, $"failed: {reason}");
                Notice = $"{reason} The page stays in {original.DisplayName()}.";
                // The engine for new tabs goes back to the one the tab is still in.
                if (ReferenceEquals(tab, selectedTab)) SetEnginePreference(original, remember: true);
                break;
            default:
                if (switches.TargetOf(tab) == kind) switches.End(tab, "abandoned");
                if (notice?.StartsWith("Opening this page in", StringComparison.Ordinal) == true) Notice = null;
                break;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>Watches a Chromium browser that is loading out of sight.</summary>
    private sealed class ChromiumLoadProbe
    {
        private readonly IChromiumBrowser browser;
        private readonly TimeProvider time;
        private bool startedLoading;

        public ChromiumLoadProbe(IChromiumBrowser browser, TimeProvider time)
        {
            this.browser = browser;
            this.time = time;
            browser.TitleChanged += OnTitle;
            browser.UrlChanged += OnUrl;
            browser.LoadStateChanged += OnLoadState;
            browser.BlockedCountChanged += OnBlocked;
            browser.Failed += OnFailed;
        }

        public string? Url { get; private set; }
        public string? Title { get; private set; }
        public bool CanGoBack { get; private set; }
        public bool CanGoForward { get; private set; }
        public int Blocked { get; private set; }
        public string? Error { get; private set; }
        /// <summary>When the page's own address was first reported (its document is committing).</summary>
        public DateTimeOffset? CommittedAt { get; private set; }
        public bool Finished { get; private set; }

        public void Detach()
        {
            browser.TitleChanged -= OnTitle;
            browser.UrlChanged -= OnUrl;
            browser.LoadStateChanged -= OnLoadState;
            browser.BlockedCountChanged -= OnBlocked;
            browser.Failed -= OnFailed;
        }

        private void OnTitle(string title) => Title = title;

        private void OnUrl(string url)
        {
            Url = url;
            if (url != BlankPage) CommittedAt ??= time.GetUtcNow();
        }

        private void OnLoadState(ChromiumLoadState state)
        {
            CanGoBack = state.CanGoBack;
            CanGoForward = state.CanGoForward;
            if (state.IsLoading)
            {
                startedLoading = true;
                Finished = false;
            }
            else if (startedLoading && Error is null)
            {
                Finished = true;
                CommittedAt ??= time.GetUtcNow();
            }
        }

        private void OnBlocked(int count) => Blocked = count;

        private void OnFailed(string reason) => Error = reason;
    }
}

/// <summary>One extension's toolbar button: what the shell draws and what a click does.</summary>
public sealed record ExtensionToolbarItem(InstalledExtension Extension, bool IsRunning, string? IconFile)
{
    public string Id => Extension.Id;
    public string Name => Extension.Name;

    /// <summary>Neither a popup nor options: a click opens Settings › Extensions.</summary>
    public bool OpensSettings => !Extension.HasPopup && !Extension.HasOptions;

    public string Help => Extension.HasPopup ? $"{Name} (opens in a Mozilla tab)" : Extension.HasOptions ? $"{Name} options" : Name;
}
