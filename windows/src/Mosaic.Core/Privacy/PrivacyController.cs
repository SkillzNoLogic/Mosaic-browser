using System.Diagnostics;
using System.Text.Json.Nodes;
using Mosaic.Core.Blocker;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Privacy;

/// <summary>A cookie as the site-data list needs it.</summary>
public sealed record ChromiumCookie(string Domain, string Name, string Path);

/// <summary>
/// What the privacy area needs from the Chromium engine (WebView2). The app implements it around
/// the shared <c>CoreWebView2Environment</c>'s profile and cookie manager; tests use a fake.
/// </summary>
/// <remarks>Called on the UI thread. No member throws for an engine that is not running; it does nothing.</remarks>
public interface IChromiumPrivacy
{
    /// <summary>Whether Chromium is available on this PC at all.</summary>
    bool IsAvailable { get; }

    /// <summary>Every cookie in the normal (on-disk) profile, or in the private one.</summary>
    Task<IReadOnlyList<ChromiumCookie>> CookiesAsync(bool privateProfile = false);

    /// <summary>
    /// Clears <paramref name="kinds"/> stored at or after <paramref name="since"/> (everything when
    /// null) from the normal profile. WebView2 takes the range for every kind.
    /// </summary>
    Task ClearAsync(IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since);

    /// <summary>Deletes one site's cookies (<paramref name="site"/> and its subdomains) and the storage of <paramref name="origins"/>.</summary>
    Task ClearSiteAsync(string site, IReadOnlyList<string> origins);

    /// <summary>
    /// Applies what can change while Chromium runs (third-party cookies, clear on exit). DNS over
    /// HTTPS is part of the environment's arguments and needs a restart; see <see cref="NeedsRestart"/>.
    /// </summary>
    void Apply(PrivacySettings settings);

    /// <summary>Whether <paramref name="settings"/> differ from what the running environment was started with in a way only a restart applies.</summary>
    bool NeedsRestart(PrivacySettings settings);

    /// <summary>Number of open private (InPrivate) browsers.</summary>
    int PrivateBrowserCount { get; }

    /// <summary>Whether the in-memory private profile currently exists (some private browser is open).</summary>
    bool HasPrivateProfile { get; }

    /// <summary>Engine details for <c>privacy.status</c>: arguments in effect, preferences, errors.</summary>
    JsonObject Status();
}

/// <summary>
/// Privacy and network security across both engines: HTTPS-only mode, tracking-parameter
/// removal, third-party cookies, DNS over HTTPS, cookies and site data, and clearing browsing data.
/// </summary>
/// <remarks>
/// <para>Port of <c>Sources/Mosaic/Features/Privacy/PrivacyController.swift</c>.</para>
/// <list type="bullet">
/// <item>Chromium: navigation rewriting in the app's WebView2 hooks (<see cref="Rewrite"/>), the
/// profile and cookie manager through <see cref="IChromiumPrivacy"/>.</item>
/// <item>Gecko: preferences and <c>Services.clearData</c> through the agent (<c>configurePrivacy</c>,
/// <c>siteData</c>, <c>clearSiteData</c>, <c>clearBrowsingData</c>).</item>
/// </list>
/// <para>Single-threaded like <see cref="BrowserModel"/>: call it on the UI thread.</para>
/// </remarks>
public sealed class PrivacyController : ObservableObject
{
    public const string GeckoPrivateNotice =
        "Private tabs run on Chromium. Mozilla private windows are not supported in Mosaic yet.";

    private readonly IGeckoEngine gecko;
    private readonly ContentBlocker blocker;
    private readonly string settingsPath;
    private readonly TimeProvider time;
    private IChromiumPrivacy? chromium;
    private PrivacySettings settings;
    private HashSet<string> sessionHttpExceptions = new(StringComparer.Ordinal);
    private IReadOnlyList<SiteDataEntry> siteData = [];
    private bool isLoadingSiteData;
    private bool siteDataIncludesGecko;
    private string? lastClearSummary;
    private JsonNode? geckoStatus;
    private (IReadOnlySet<BrowsingDataKind> Kinds, DateTimeOffset? Since)? pendingGeckoClear;
    private PublicSuffixList? suffixList;
    private Func<IEnumerable<string>> openPages = () => [];

    public PrivacyController(IGeckoEngine gecko, ContentBlocker blocker, string? settingsPath = null, TimeProvider? time = null)
    {
        this.gecko = gecko;
        this.blocker = blocker;
        this.settingsPath = settingsPath ?? DefaultPath;
        this.time = time ?? TimeProvider.System;
        settings = Read(this.settingsPath);
    }

    /// <summary><c>privacy.json</c> under <see cref="AppPaths.DataRoot"/>: <see cref="PrivacySettings"/> in macOS's JSON form.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "privacy.json");

    public PrivacySettings Settings { get => settings; private set => Set(ref settings, value); }

    /// <summary>Hosts the user let through over plain HTTP this session (HTTPS-only page).</summary>
    public IReadOnlySet<string> SessionHttpExceptions => sessionHttpExceptions;

    public IReadOnlyList<SiteDataEntry> SiteData { get => siteData; private set => Set(ref siteData, value); }

    public bool IsLoadingSiteData { get => isLoadingSiteData; private set => Set(ref isLoadingSiteData, value); }

    /// <summary>Whether the last site-data listing could include Gecko's data.</summary>
    public bool SiteDataIncludesGecko { get => siteDataIncludesGecko; private set => Set(ref siteDataIncludesGecko, value); }

    /// <summary>The last clearing's outcome, for the settings page.</summary>
    public string? LastClearSummary { get => lastClearSummary; private set => Set(ref lastClearSummary, value); }

    /// <summary>Gecko's privacy preferences as last reported by the agent.</summary>
    public JsonNode? GeckoStatus { get => geckoStatus; private set => Set(ref geckoStatus, value); }

    /// <summary>The Chromium side, once the app has provided it.</summary>
    public IChromiumPrivacy? Chromium => chromium;

    /// <summary>Whether Chromium tabs will only pick up the DNS setting after Mosaic restarts.</summary>
    public bool ChromiumNeedsRestart => chromium?.NeedsRestart(settings) == true;

    // ---- Lifecycle ----

    /// <summary>
    /// Connects the Chromium side and applies the settings to it, and hooks the Gecko side into
    /// each Gecko start. Call once, from the app's start-up.
    /// </summary>
    /// <param name="chromiumPrivacy">The app's WebView2 side; null where Chromium is not part of the build.</param>
    /// <param name="openPages">The addresses of open non-private tabs (their origins carry ports cookies do not).</param>
    public void Start(IChromiumPrivacy? chromiumPrivacy, Func<IEnumerable<string>>? openPages = null)
    {
        chromium = chromiumPrivacy;
        if (openPages is not null) this.openPages = openPages;
        chromium?.Apply(settings);
        var previous = gecko.OnStarted;
        gecko.OnStarted = async () =>
        {
            if (previous is not null) await previous();
            await ConfigureGeckoAsync();
        };
        if (gecko.Status == Gecko.GeckoStatus.Running) _ = ConfigureGeckoAsync();
    }

    /// <summary>The options <c>configurePrivacy</c> takes (see agent.sys.mjs).</summary>
    public JsonObject GeckoOptions()
    {
        var dns = new JsonObject();
        foreach (var (name, value) in settings.DnsOverHttps.GeckoPreferences)
        {
            dns[name] = value switch
            {
                int number => JsonValue.Create(number),
                string text => JsonValue.Create(text),
                _ => JsonValue.Create(value.ToString()),
            };
        }
        return new JsonObject
        {
            ["httpsOnly"] = settings.HttpsOnly,
            ["httpsExceptions"] = new JsonArray([.. settings.HttpsExceptions.Concat(sessionHttpExceptions.Order(StringComparer.Ordinal)).Select(site => JsonValue.Create(site))]),
            ["stripTracking"] = settings.StripTrackingParameters,
            ["stripList"] = new JsonArray([.. TrackingParameters.AllNames.Select(name => JsonValue.Create(name))]),
            ["blockThirdPartyCookies"] = settings.BlockThirdPartyCookies,
            ["clearOnExit"] = settings.ClearCookiesOnExit,
            ["dnsPreferences"] = dns,
            ["cosmetic"] = blocker.CosmeticEnabled,
        };
    }

    /// <summary>Called each time Gecko has started (before any Mosaic tab loads there), and after a change.</summary>
    public async Task ConfigureGeckoAsync()
    {
        if (gecko.Status != Gecko.GeckoStatus.Running) return;
        try
        {
            GeckoStatus = await gecko.CallAsync("configurePrivacy", GeckoOptions());
            if (settings.ClearCookiesOnExit)
            {
                // Backstop for "clear on exit": whatever Gecko kept from the last session goes.
                await gecko.CallAsync("clearBrowsingData", new JsonArray("cookiesAndSiteData"), 0);
            }
            if (pendingGeckoClear is { } pending)
            {
                pendingGeckoClear = null;
                await gecko.CallAsync("clearBrowsingData", KindsJson(pending.Kinds), pending.Since?.ToUnixTimeMilliseconds() ?? 0);
            }
        }
        catch (Exception error)
        {
            Trace.WriteLine($"[Privacy] Could not apply privacy settings to Gecko: {error.Message}");
        }
    }

    // ---- Settings ----

    /// <summary>Changes settings, saves them and applies them to both engines.</summary>
    public void Update(Func<PrivacySettings, PrivacySettings> change)
    {
        var next = change(settings);
        if (next == settings) return;
        var restartBefore = ChromiumNeedsRestart;
        Settings = next;
        Save();
        chromium?.Apply(next);
        if (restartBefore != ChromiumNeedsRestart) Notify(nameof(ChromiumNeedsRestart));
        _ = ConfigureGeckoAsync();
    }

    private void Save()
    {
        var json = settings.ToJson();
        var path = settingsPath;
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The choice lasts for this run only.
        }
    }

    private static PrivacySettings Read(string path)
    {
        try
        {
            return File.Exists(path) ? PrivacySettings.FromJson(File.ReadAllText(path)) : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    // ---- Navigation (HTTPS-only, tracking parameters) ----

    /// <summary>What Chromium should load instead of <paramref name="url"/>, if anything.</summary>
    public string? Rewrite(string url, bool allowInsecure = false) =>
        NavigationPolicy.Rewrite(url, settings, sessionHttpExceptions, allowInsecure);

    /// <summary>The user chose "Continue to HTTP Site" on the HTTPS-only page for <paramref name="host"/>.</summary>
    public void AllowHttpThisSession(string host)
    {
        if (BlockerSettings.Normalize(host) is not { } site || !sessionHttpExceptions.Add(site)) return;
        Notify(nameof(SessionHttpExceptions));
        _ = ConfigureGeckoAsync();
    }

    // ---- Cookies and site data ----

    /// <summary>Lists every site with cookies or storage in either engine.</summary>
    public async Task LoadSiteDataAsync()
    {
        IsLoadingSiteData = true;
        try
        {
            if (suffixList is null && blocker.Store.SuffixListPath is { } path)
            {
                suffixList = await Task.Run(() => File.Exists(path) ? new PublicSuffixList(File.ReadAllText(path)) : null);
            }
            List<SiteDataRecord> records = [];
            if (chromium is { IsAvailable: true })
            {
                foreach (var cookie in await chromium.CookiesAsync())
                {
                    records.Add(new SiteDataRecord(SiteDataEngine.Chromium, cookie.Domain, Cookies: 1));
                }
            }
            SiteDataIncludesGecko = gecko.Status == Gecko.GeckoStatus.Running;
            if (SiteDataIncludesGecko)
            {
                try
                {
                    if (await gecko.CallAsync("siteData") is JsonArray hosts)
                    {
                        foreach (var host in hosts.OfType<JsonObject>())
                        {
                            records.Add(new SiteDataRecord(SiteDataEngine.Gecko, Text(host["host"]),
                                (int)Number(host["cookies"]), Number(host["bytes"])));
                        }
                    }
                }
                catch (Exception error)
                {
                    Trace.WriteLine($"[Privacy] Gecko's site data could not be read: {error.Message}");
                }
            }
            var list = suffixList;
            SiteData = await Task.Run(() => SiteDataSummary.Merge(records, list));
        }
        finally
        {
            IsLoadingSiteData = false;
        }
    }

    /// <summary>Deletes one site's cookies, storage and (Gecko) cache in both engines.</summary>
    public async Task ClearSiteDataAsync(SiteDataEntry entry)
    {
        if (chromium is { IsAvailable: true })
        {
            var origins = entry.ChromiumOrigins.Concat(OpenOrigins(entry.Site)).Distinct().Order(StringComparer.Ordinal).ToList();
            await chromium.ClearSiteAsync(entry.Site, origins);
        }
        if (gecko.Status == Gecko.GeckoStatus.Running)
        {
            try
            {
                await gecko.CallAsync("clearSiteData", entry.Site);
            }
            catch (Exception error)
            {
                Trace.WriteLine($"[Privacy] Gecko could not clear {entry.Site}: {error.Message}");
            }
        }
        await LoadSiteDataAsync();
    }

    public async Task ClearAllSiteDataAsync()
    {
        var kinds = new HashSet<BrowsingDataKind> { BrowsingDataKind.CookiesAndSiteData };
        if (chromium is { IsAvailable: true }) await chromium.ClearAsync(kinds, null);
        await ClearGeckoAsync(kinds, null);
        await LoadSiteDataAsync();
    }

    /// <summary>Origins (with ports) of open non-private web pages, on <paramref name="site"/> or all.</summary>
    private IEnumerable<string> OpenOrigins(string? site) =>
        openPages().Select(page => Uri.TryCreate(page, UriKind.Absolute, out var parsed) ? parsed : null)
            .OfType<Uri>()
            .Where(page => page.Scheme is "http" or "https")
            .Where(page => site is null || page.Host == site || page.Host.EndsWith("." + site, StringComparison.Ordinal))
            .Select(page => page.GetLeftPart(UriPartial.Authority).ToLowerInvariant());

    // ---- Clear browsing data ----

    /// <summary>
    /// Clears <paramref name="kinds"/> from both engines and from every registered Mosaic service
    /// (<see cref="BrowsingDataRegistry"/>: history, downloads list). Returns a summary.
    /// </summary>
    public async Task<string> ClearBrowsingDataAsync(IReadOnlySet<BrowsingDataKind> kinds, BrowsingDataTimeRange range, BrowsingDataRegistry? registry = null)
    {
        var since = range.StartDate(time.GetUtcNow());
        List<string> cleared = [];
        if (chromium is { IsAvailable: true })
        {
            await chromium.ClearAsync(kinds, since);
            cleared.Add("Chromium");
        }
        cleared.Add(await ClearGeckoAsync(kinds, since) ? "Mozilla" : "Mozilla (when it next starts)");
        cleared.AddRange(await (registry ?? BrowsingDataRegistry.Shared).ClearAsync(kinds, since));
        var what = kinds.Count == 1 ? kinds.First().Title().ToLowerInvariant() : $"{kinds.Count} kinds of data";
        var summary = $"Cleared {what} ({range.Title().ToLowerInvariant()}) from {string.Join(", ", cleared)}.";
        LastClearSummary = summary;
        if (kinds.Contains(BrowsingDataKind.CookiesAndSiteData) && siteData.Count > 0) await LoadSiteDataAsync();
        return summary;
    }

    /// <summary>Clears Gecko now, or when it next starts if it is not running. True when it was cleared now.</summary>
    private async Task<bool> ClearGeckoAsync(IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since)
    {
        if (gecko.Status == Gecko.GeckoStatus.Running)
        {
            try
            {
                await gecko.CallAsync("clearBrowsingData", KindsJson(kinds), since?.ToUnixTimeMilliseconds() ?? 0);
                return true;
            }
            catch (Exception error)
            {
                Trace.WriteLine($"[Privacy] Gecko could not clear browsing data: {error.Message}");
            }
        }
        // Merged with anything already waiting; the wider range wins.
        if (pendingGeckoClear is { } waiting)
        {
            var union = waiting.Kinds.Union(kinds).ToHashSet();
            var earliest = waiting.Since is null || since is null ? null : (DateTimeOffset?)(waiting.Since < since ? waiting.Since : since);
            pendingGeckoClear = (union, earliest);
        }
        else
        {
            pendingGeckoClear = (kinds, since);
        }
        return false;
    }

    /// <summary>Whether a Gecko clear is waiting for Gecko's next start.</summary>
    public bool HasPendingGeckoClear => pendingGeckoClear is not null;

    private static JsonArray KindsJson(IEnumerable<BrowsingDataKind> kinds) => new([.. kinds.Select(kind => JsonValue.Create(kind.Id()))]);

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static long Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? (long)number
            : 0;
}
