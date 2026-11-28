using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Mosaic.Core.Blocker;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Browser;

/// <summary>Where the content blocker is in its life.</summary>
public enum BlockerStatus
{
    /// <summary>The engine was not bundled into this build.</summary>
    Unavailable,
    Loading,
    /// <summary>Filtering; <see cref="ContentBlocker.FilterCount"/> says with how many filters.</summary>
    Ready,
    /// <summary><see cref="ContentBlocker.FailureMessage"/> says why.</summary>
    Failed,
}

/// <summary>Where the content blocker finds its files, and how it reaches the network.</summary>
public sealed class ContentBlockerOptions
{
    /// <summary>
    /// The blocker as shipped: <c>ublock-engine.js</c> and the list snapshot in <c>lists\</c>.
    /// Default: <see cref="AppPaths.BlockerResources"/>. Without the bundle the blocker is
    /// <see cref="BlockerStatus.Unavailable"/>.
    /// </summary>
    public string ResourcesDirectory { get; init; } = AppPaths.BlockerResources;

    /// <summary>Where the filter lists live and are refreshed. Default: <see cref="AppPaths.BlockerLists"/>.</summary>
    public string ListsDirectory { get; init; } = AppPaths.BlockerLists;

    /// <summary>Sends the list downloads; tests pass a stub. Default: a new handler per update.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>How long after start-up stale lists wait to be refreshed, so the browser can settle first.</summary>
    public TimeSpan RefreshDelay { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Mosaic's built-in ad and tracker blocker.
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>Sources/Mosaic/Model/ContentBlocker.swift</c>. It is part of the browser, not an
/// extension: uBlock Origin's network filtering engine, built from uBO's source, with uBO's default
/// filter lists. The same engine and lists serve both rendering engines:
/// </para>
/// <list type="bullet">
/// <item>Chromium tabs: WebView2 asks <see cref="Filter"/> (an engine hosted in this process)
/// before each request.</item>
/// <item>Mozilla tabs: the Gecko agent loads the engine inside Gecko and cancels blocked requests
/// there (<c>configureBlocker</c> in <c>agent.sys.mjs</c>).</item>
/// </list>
/// <para>
/// What it does not do: cosmetic filtering (hiding leftover page elements) and scriptlet injection,
/// which in uBO live in its content scripts.
/// </para>
/// <para>
/// Single-threaded like <see cref="BrowserModel"/>, which owns it: call it on the UI thread. Only
/// compiling the engine and downloading the lists happen elsewhere, and their results come back
/// to that thread.
/// </para>
/// </remarks>
public sealed partial class ContentBlocker : ObservableObject // privacy: ContentBlocker.Privacy.cs (cosmetic filtering, lists, My filters, dashboard)
{
    /// <summary>Lists older than this are refreshed in the background.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(2);

    private readonly IGeckoEngine gecko;
    private readonly BrowserSettings browserSettings;
    private readonly ContentBlockerOptions options;
    private readonly TimeProvider time;
    private readonly FilterListStore store;
    private int buildGeneration;
    private bool isShutDown;

    private BlockerStatus status = BlockerStatus.Loading;
    private BlockerSettings settings;
    private int filterCount;
    private string? failureMessage;
    private bool isUpdating;
    private DateTimeOffset? listsDate;
    private string? engineVersion;

    public ContentBlocker(IGeckoEngine gecko, BrowserSettings browserSettings, ContentBlockerOptions? options = null, TimeProvider? time = null)
    {
        this.gecko = gecko;
        this.browserSettings = browserSettings;
        this.options = options ?? new ContentBlockerOptions();
        this.time = time ?? TimeProvider.System;
        store = new FilterListStore(this.options.ListsDirectory);
        settings = new BlockerSettings(browserSettings.BlockerEnabled ?? true, browserSettings.BlockerAllowlist);
        Filter.Use(settings);
    }

    /// <summary>What WebView2 asks before each request; safe to use from any thread.</summary>
    public RequestFilter Filter { get; } = new();

    public BlockerStatus Status
    {
        get => status;
        private set
        {
            if (!Set(ref status, value)) return;
            Notify(nameof(IsAvailable));
            Notify(nameof(StatusLine));
        }
    }

    /// <summary>False when the engine was not bundled into this build.</summary>
    public bool IsAvailable => status != BlockerStatus.Unavailable;

    public BlockerSettings Settings
    {
        get => settings;
        private set
        {
            if (Set(ref settings, value)) Notify(nameof(IsEnabled));
        }
    }

    public bool IsEnabled => settings.IsEnabled;

    /// <summary>Network filters in effect for Chromium tabs, once <see cref="Status"/> is <see cref="BlockerStatus.Ready"/>.</summary>
    public int FilterCount
    {
        get => filterCount;
        private set
        {
            if (Set(ref filterCount, value)) Notify(nameof(StatusLine));
        }
    }

    /// <summary>User-presentable reason while <see cref="Status"/> is <see cref="BlockerStatus.Failed"/>.</summary>
    public string? FailureMessage
    {
        get => failureMessage;
        private set
        {
            if (Set(ref failureMessage, value)) Notify(nameof(StatusLine));
        }
    }

    public bool IsUpdating { get => isUpdating; private set => Set(ref isUpdating, value); }

    /// <summary>When the filter lists were last refreshed (the oldest of them); null if any is missing.</summary>
    public DateTimeOffset? ListsDate
    {
        get => listsDate;
        private set
        {
            if (Set(ref listsDate, value)) Notify(nameof(ListsLine));
        }
    }

    /// <summary>Version of uBlock Origin the engine was built from.</summary>
    public string? EngineVersion
    {
        get => engineVersion;
        private set
        {
            if (Set(ref engineVersion, value)) Notify(nameof(StatusLine));
        }
    }

    /// <summary>The engine's state in a line: "241,996 filters · uBO 1.77.2", "Loading filters…".</summary>
    public string StatusLine => status switch
    {
        BlockerStatus.Unavailable => "Unavailable",
        BlockerStatus.Loading => "Loading filters…",
        BlockerStatus.Failed => failureMessage ?? "The content blocker could not start.",
        _ => string.Format(CultureInfo.CurrentCulture, "{0:N0} filters", filterCount)
             + (engineVersion is { } version ? $" · uBO {version}" : ""),
    };

    /// <summary>How fresh the lists are: "Lists updated 3 hours ago".</summary>
    /// <remarks>Relative to when it is read; see <see cref="RefreshListsLine"/>.</remarks>
    public string ListsLine => listsDate is { } date
        ? $"Lists updated {RelativeTime(date, time.GetUtcNow())}"
        : "Filter lists not downloaded yet";

    /// <summary>The latest engine build; tests wait on it.</summary>
    internal Task Building { get; private set; } = Task.CompletedTask;

    /// <summary>The pending background refresh of stale lists, if one was scheduled; tests wait on it.</summary>
    internal Task Refreshing { get; private set; } = Task.CompletedTask;

    private string BundlePath => Path.Combine(options.ResourcesDirectory, "ublock-engine.js");

    // ---- Lifecycle ----

    /// <summary>
    /// Installs the shipped lists if they are missing, starts building the engine in the
    /// background and schedules a refresh of stale lists. Call once, before any page loads.
    /// </summary>
    public void Start()
    {
        if (!File.Exists(BundlePath))
        {
            Status = BlockerStatus.Unavailable;
            return;
        }
        try
        {
            store.Seed(Path.Combine(options.ResourcesDirectory, "lists"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Fail($"The filter lists could not be installed: {error.Message}");
            return;
        }
        ListsDate = store.OldestModification();
        RebuildEngine();

        // Refresh stale lists once the browser has settled.
        if (listsDate is not { } date || time.GetUtcNow() - date > RefreshInterval) Refreshing = RefreshLaterAsync();
    }

    private async Task RefreshLaterAsync()
    {
        await Task.Delay(options.RefreshDelay, time);
        if (!isShutDown) await UpdateListsAsync();
    }

    /// <summary>
    /// Compiles the lists on disk into a new engine and swaps it in. The previous engine keeps
    /// answering until the new one is ready.
    /// </summary>
    private void RebuildEngine()
    {
        var generation = ++buildGeneration;
        if (status != BlockerStatus.Ready) Status = BlockerStatus.Loading;
        Building = BuildAsync(generation);
    }

    private async Task BuildAsync(int generation)
    {
        BlockerEngine engine;
        try
        {
            var bundle = BundlePath;
            engine = await Task.Run(() =>
            {
                var suffixList = store.SuffixList()
                    ?? throw new BlockerEngineException("The content blocker’s engine reported an error: the Public Suffix List is missing");
                return new BlockerEngine(bundle, suffixList, store.LoadLists());
            });
        }
        catch (Exception error)
        {
            // Whatever stopped it (a damaged bundle, V8 failing to load on this PC), Mosaic carries
            // on without filtering Chromium tabs, and the popover says why.
            if (generation == buildGeneration && !isShutDown)
            {
                Fail(error is BlockerEngineException ? error.Message : $"The content blocker could not start: {error.Message}");
            }
            return;
        }

        if (generation != buildGeneration || isShutDown)
        {
            engine.Dispose();
            return;
        }
        Filter.Use(engine)?.Dispose();
        EngineVersion = engine.Version;
        FilterCount = engine.FilterCount;
        FailureMessage = null;
        Status = BlockerStatus.Ready;
    }

    private void Fail(string message)
    {
        FailureMessage = message;
        Status = BlockerStatus.Failed;
    }

    /// <summary>Loads the engine into Gecko. Called each time Gecko has started, and after the lists change.</summary>
    public async Task ConfigureGeckoAsync()
    {
        if (status == BlockerStatus.Unavailable || gecko.Status != GeckoStatus.Running) return;
        try
        {
            await gecko.CallAsync(
                "configureBlocker", BundlePath, store.SuffixListPath, JsonList(store.AvailableListPaths()),
                settings.IsEnabled, JsonList(settings.Allowlist.Order(StringComparer.Ordinal)));
        }
        catch (Exception error)
        {
            // Mozilla tabs go unfiltered until the next start or list update; nothing else is affected.
            Trace.WriteLine($"[Blocker] Could not load the content blocker into Gecko: {error.Message}");
        }
    }

    /// <summary>Downloads fresh filter lists and rebuilds both engines from them.</summary>
    public async Task UpdateListsAsync()
    {
        if (status == BlockerStatus.Unavailable || isUpdating || isShutDown) return;
        IsUpdating = true;
        try
        {
            var result = await store.UpdateAsync(options.HttpHandler);
            if (result.Updated.Count == 0 || isShutDown) return;
            ListsDate = store.OldestModification();
            RebuildEngine();
            await ConfigureGeckoAsync();
        }
        finally
        {
            IsUpdating = false;
        }
    }

    /// <summary>Releases the engine; anything still running finishes without effect. Called once, at exit.</summary>
    public void Shutdown()
    {
        if (isShutDown) return;
        isShutDown = true;
        Filter.Use((BlockerEngine?)null)?.Dispose();
    }

    /// <summary>Announces <see cref="ListsLine"/> as changed, since "3 hours ago" goes stale by itself.</summary>
    public void RefreshListsLine() => Notify(nameof(ListsLine));

    // ---- Settings ----

    public void SetEnabled(bool enabled)
    {
        if (settings.IsEnabled == enabled) return;
        Settings = settings.WithEnabled(enabled);
        SettingsChanged();
    }

    /// <summary>The site a page belongs to, if the blocker applies to pages like it.</summary>
    public static string? SiteOfPage(string url) => BlockerSettings.SiteOfPage(url);

    /// <summary>Whether requests from this page are being filtered right now.</summary>
    public bool IsFiltering(string pageUrl) => status != BlockerStatus.Unavailable && settings.Filters(pageUrl);

    /// <summary>Turns blocking on or off for the site <paramref name="pageUrl"/> belongs to.</summary>
    public void SetBlocking(bool blocking, string pageUrl)
    {
        if (SiteOfPage(pageUrl) is not { } site) return;
        Settings = settings.WithAllowlisted(!blocking, site);
        SettingsChanged();
    }

    private void SettingsChanged()
    {
        browserSettings.BlockerEnabled = settings.IsEnabled;
        browserSettings.BlockerAllowlist = [.. settings.Allowlist.Order(StringComparer.Ordinal)];
        browserSettings.Save();
        Filter.Use(settings);
        gecko.Send("setBlockerOptions", settings.IsEnabled, JsonList(browserSettings.BlockerAllowlist));
    }

    private static JsonArray JsonList(IEnumerable<string> items) => new([.. items.Select(item => JsonValue.Create(item))]);

    /// <summary>
    /// "just now", "5 minutes ago", "3 hours ago", "2 days ago": the gist of macOS's
    /// <c>RelativeDateTimeFormatter</c> in its full style, which .NET has no counterpart for.
    /// </summary>
    internal static string RelativeTime(DateTimeOffset date, DateTimeOffset now)
    {
        var elapsed = now - date;
        static string Ago(double count, string unit)
        {
            var whole = (int)Math.Floor(count);
            return whole == 1 ? $"1 {unit} ago" : $"{whole} {unit}s ago";
        }
        return elapsed switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => Ago(elapsed.TotalMinutes, "minute"),
            { TotalDays: < 1 } => Ago(elapsed.TotalHours, "hour"),
            { TotalDays: < 7 } => Ago(elapsed.TotalDays, "day"),
            { TotalDays: < 30 } => Ago(elapsed.TotalDays / 7, "week"),
            { TotalDays: < 365 } => Ago(elapsed.TotalDays / 30, "month"),
            _ => Ago(elapsed.TotalDays / 365, "year"),
        };
    }
}
