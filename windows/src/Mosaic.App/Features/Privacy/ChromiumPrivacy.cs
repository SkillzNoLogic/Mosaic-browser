using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core.Blocker;
using Mosaic.Core.Privacy;

namespace Mosaic.App.Features.Privacy;

/// <summary>
/// The privacy area's side of the Chromium engine (WebView2): HTTPS-only and tracking-parameter
/// stripping on navigation, the HTTPS-only page, cosmetic filtering in every frame, third-party
/// cookie controls, private (InPrivate) browsers, DNS over HTTPS through the environment's
/// arguments, and cookies/site data/clearing through the profile (<see cref="IChromiumPrivacy"/>).
/// </summary>
/// <remarks>
/// <para>The winui area's WebView2 code calls the hooks (all static, so it needs no instance):</para>
/// <list type="number">
/// <item><see cref="BrowserArguments"/> when it builds the environment options, and
/// <see cref="EnvironmentCreated"/> with the environment task.</item>
/// <item><see cref="PrivateControllerOptions"/> for a private tab's
/// <c>WebView2.EnsureCoreWebView2Async(environment, options)</c>.</item>
/// <item><see cref="AttachAsync"/> once each tab's <c>CoreWebView2</c> exists, before its first navigation.</item>
/// </list>
/// <para>Counterpart of <c>MosaicPrivacyCEF</c> in macOS's CEF bridge. Everything runs on the UI thread.</para>
/// </remarks>
public sealed class ChromiumPrivacy : IChromiumPrivacy
{
    /// <summary>The profile name private browsers share; all InPrivate controllers of one name share one in-memory profile.</summary>
    public const string PrivateProfileName = "MosaicPrivate";

    private static ChromiumPrivacy? current;
    private static Task<CoreWebView2Environment>? environment;
    private static PrivacySettings? startedWith;

    private readonly Func<PrivacyController> privacy;
    private readonly Func<RequestFilter?> filter;
    private readonly Func<nint> windowHandle;
    private readonly List<Attached> attached = [];
    private readonly Dictionary<string, string> preferenceErrors = new(StringComparer.Ordinal);
    private CoreWebView2Controller? dataController;
    private PrivacySettings? applied;
    private bool clearedAtStart;

    /// <param name="privacy">The model's privacy controller.</param>
    /// <param name="filter">The content blocker's request filter (null while the blocker is not part of the build).</param>
    /// <param name="windowHandle">The main window, for the hidden browser data operations use when no tab has one.</param>
    public ChromiumPrivacy(Func<PrivacyController> privacy, Func<RequestFilter?> filter, Func<nint> windowHandle, bool isAvailable)
    {
        this.privacy = privacy;
        this.filter = filter;
        this.windowHandle = windowHandle;
        IsAvailable = isAvailable;
        current = this;
    }

    private sealed record Attached(CoreWebView2 Core, bool IsPrivate, PageState State);

    /// <summary>Per-browser navigation state for the HTTPS-only page.</summary>
    private sealed class PageState
    {
        /// <summary>Address Mosaic upgraded to https → the http address it came from.</summary>
        public Dictionary<string, string> Upgraded { get; } = new(StringComparer.Ordinal);
        /// <summary>The next navigation to this address may go over HTTP (the user said so).</summary>
        public string? AllowInsecure { get; set; }
        public string? FailedHttpsUrl { get; set; }
    }

    // ---- Hooks for the winui area ----

    /// <summary>
    /// <paramref name="arguments"/> with the privacy area's switches merged in (DNS over HTTPS).
    /// Read once, when the environment is created; a later change needs a restart.
    /// </summary>
    public static string BrowserArguments(string? arguments, PrivacySettings settings)
    {
        startedWith = settings;
        return Mosaic.Core.Privacy.BrowserArguments.Merge(arguments, settings.DnsOverHttps.WebView2Feature is { } feature ? [feature] : []);
    }

    /// <summary>The shared environment, for data operations when no tab is open.</summary>
    public static void EnvironmentCreated(Task<CoreWebView2Environment> created) => environment = created;

    /// <summary>Controller options for a private browser: InPrivate, in the shared private profile.</summary>
    public static CoreWebView2ControllerOptions PrivateControllerOptions(CoreWebView2Environment environment)
    {
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        options.ProfileName = PrivateProfileName;
        return options;
    }

    /// <summary>
    /// Hooks one tab's browser into the privacy features. Call after <c>EnsureCoreWebView2Async</c>
    /// and before the first navigation. Never throws.
    /// </summary>
    public static Task AttachAsync(CoreWebView2 core, bool isPrivate) =>
        current?.AttachCoreAsync(core, isPrivate) ?? Task.CompletedTask;

    private async Task AttachCoreAsync(CoreWebView2 core, bool isPrivate)
    {
        var state = new PageState();
        var entry = new Attached(core, isPrivate, state);
        attached.Add(entry);
        try
        {
            core.NavigationStarting += (_, navigation) => OnNavigationStarting(core, state, navigation);
            core.NavigationCompleted += (_, completed) => OnNavigationCompleted(core, state, completed);
            core.WebMessageReceived += (_, message) => OnWebMessage(core, state, message);
            core.ContentLoading += (_, _) => InjectCosmetics(core, core.Source, core.Source, isMainFrame: true);
            core.DOMContentLoaded += (_, _) => InjectCosmetics(core, core.Source, core.Source, isMainFrame: true, newDocument: false);
            core.FrameCreated += (_, created) => WatchFrame(core, created.Frame);
            await ApplyCookieControlsAsync(core);
            if (!isPrivate && !clearedAtStart && privacy().Settings.ClearCookiesOnExit)
            {
                // Backstop for "clear on exit": whatever Chromium kept from the last session goes
                // before the first page of this one loads.
                clearedAtStart = true;
                await ClearProfileAsync(core.Profile, CoreWebView2BrowsingDataKinds.AllSite, null);
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            Trace.WriteLine($"[Privacy] Could not attach to a Chromium browser: {error.Message}");
        }
    }

    /// <summary>Forgets a browser that closed. Call from the tab's close.</summary>
    public static void Detach(CoreWebView2 core) => current?.attached.RemoveAll(entry => ReferenceEquals(entry.Core, core));

    // ---- Navigation: HTTPS-only and tracking parameters ----

    private void OnNavigationStarting(CoreWebView2 core, PageState state, CoreWebView2NavigationStartingEventArgs navigation)
    {
        var url = navigation.Uri;
        var allowInsecure = state.AllowInsecure == url;
        if (allowInsecure) state.AllowInsecure = null;
        if (privacy().Rewrite(url, allowInsecure) is not { } rewritten) return;
        navigation.Cancel = true;
        if (rewritten.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            state.Upgraded[rewritten] = NavigationPolicy.Rewrite(url, privacy().Settings with { HttpsOnly = false }) ?? url;
            if (state.Upgraded.Count > 32) state.Upgraded.Remove(state.Upgraded.Keys.First());
        }
        Navigate(core, rewritten);
    }

    private void OnNavigationCompleted(CoreWebView2 core, PageState state, CoreWebView2NavigationCompletedEventArgs completed)
    {
        if (completed.IsSuccess) return;
        var url = core.Source;
        if (!state.Upgraded.TryGetValue(url, out var http) || !IsConnectionFailure(completed.WebErrorStatus)) return;
        state.FailedHttpsUrl = url;
        try
        {
            core.NavigateToString(HttpsOnlyPage(http, completed.WebErrorStatus));
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        {
            Trace.WriteLine($"[Privacy] Could not show the HTTPS-only page: {error.Message}");
        }
    }

    /// <summary>Failures that mean "this site has no working HTTPS", as opposed to "the site is down".</summary>
    private static bool IsConnectionFailure(CoreWebView2WebErrorStatus status) => status is
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
        or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked
        or CoreWebView2WebErrorStatus.CertificateIsInvalid or CoreWebView2WebErrorStatus.ConnectionAborted
        or CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.CannotConnect
        or CoreWebView2WebErrorStatus.ServerUnreachable or CoreWebView2WebErrorStatus.Timeout
        or CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.Unknown;

    private void OnWebMessage(CoreWebView2 core, PageState state, CoreWebView2WebMessageReceivedEventArgs message)
    {
        string? json;
        try
        {
            json = message.WebMessageAsJson;
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        {
            return;
        }
        if (JsonNode.Parse(json ?? "null") is not JsonObject request || request["mosaic"]?.GetValue<string>() != "continueHttp") return;
        // Only the page Mosaic itself put up may ask, and only for the address it failed on.
        if (state.FailedHttpsUrl is not { } failed || !state.Upgraded.TryGetValue(failed, out var http)) return;
        if (Uri.TryCreate(http, UriKind.Absolute, out var parsed)) privacy().AllowHttpThisSession(parsed.Host);
        state.AllowInsecure = http;
        state.FailedHttpsUrl = null;
        Navigate(core, http);
    }

    private static void Navigate(CoreWebView2 core, string url)
    {
        try
        {
            core.Navigate(url);
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        {
            Trace.WriteLine($"[Privacy] Could not load {url}: {error.Message}");
        }
    }

    /// <summary>Mosaic's HTTPS-only page (Firefox's has the same choice): go back, or continue over HTTP for this session.</summary>
    internal static string HttpsOnlyPage(string httpUrl, CoreWebView2WebErrorStatus status)
    {
        var host = Uri.TryCreate(httpUrl, UriKind.Absolute, out var parsed) ? parsed.Host : httpUrl;
        var url = JsonSerializer.Serialize(httpUrl);
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Secure connection not available</title>
            <meta name="color-scheme" content="light dark">
            <style>body{font:15px/1.5 "Segoe UI Variable Text","Segoe UI",sans-serif;max-width:560px;margin:12vh auto;padding:0 24px}
            h1{font-size:24px;font-weight:600}button{font:inherit;padding:6px 16px;border-radius:4px;margin-right:8px;cursor:pointer}
            .primary{background:#005fb8;color:#fff;border:1px solid #005fb8}code{font-size:13px}</style></head><body>
            <h1>Secure connection not available</h1>
            <p>HTTPS-Only Mode is on, and <strong>{{WebUtility.HtmlEncode(host)}}</strong> does not support a secure connection
            (<code>{{WebUtility.HtmlEncode(status.ToString())}}</code>).</p>
            <p>If you continue, the page is loaded over plain HTTP: anyone on the network could read or change it.</p>
            <p><button class="primary" id="back">Go Back</button><button id="continue">Continue to HTTP Site</button></p>
            <script>
            document.getElementById('back').onclick = () => history.length > 1 ? history.back() : location.replace('about:blank');
            document.getElementById('continue').onclick = () => chrome.webview.postMessage({ mosaic: 'continueHttp', url: {{url}} });
            </script></body></html>
            """;
    }

    // ---- Cosmetic filtering ----

    private void WatchFrame(CoreWebView2 core, CoreWebView2Frame frame)
    {
        string? frameUrl = null;
        try
        {
            frame.NavigationStarting += (_, navigation) => frameUrl = navigation.Uri;
            frame.ContentLoading += (_, _) => InjectCosmetics(frame, frameUrl, core.Source);
            frame.DOMContentLoaded += (_, _) => InjectCosmetics(frame, frameUrl, core.Source);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            // The frame went away while being set up.
        }
    }

    private void InjectCosmetics(CoreWebView2 core, string frameUrl, string pageUrl, bool isMainFrame, bool newDocument = true)
    {
        // ContentLoading starts the page's dashboard log over (a new document); DOMContentLoaded
        // only repeats the injection in case the document was not ready for it yet. The script
        // itself takes effect once per document.
        if (filter() is not { } requests) return;
        if (requests.CosmeticScript(frameUrl, pageUrl, isMainFrame, newDocument) is not { } script) return;
        try
        {
            _ = core.ExecuteScriptAsync(script);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
        }
    }

    private void InjectCosmetics(CoreWebView2Frame frame, string? frameUrl, string pageUrl)
    {
        if (frameUrl is null || filter() is not { } requests) return;
        if (requests.CosmeticScript(frameUrl, pageUrl, isMainFrame: false) is not { } script) return;
        try
        {
            _ = frame.ExecuteScriptAsync(script);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
        }
    }

    // ---- Third-party cookies ----

    /// <summary>
    /// Third-party cookie blocking for one browser. WebView2 has no API for Chromium's cookie
    /// controls preference, so Mosaic uses the DevTools protocol's <c>Network.setCookieControls</c>
    /// (per browser, takes effect from the next load), and falls back to Edge's strict tracking
    /// prevention, which blocks most third-party trackers' cookies, where the protocol method is missing.
    /// </summary>
    private async Task ApplyCookieControlsAsync(CoreWebView2 core)
    {
        var block = privacy().Settings.BlockThirdPartyCookies;
        try
        {
            var parameters = new JsonObject
            {
                ["enableThirdPartyCookieRestriction"] = block,
                ["disableThirdPartyCookieMetadata"] = false,
                ["disableThirdPartyCookieHeuristics"] = block,
            };
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Network.setCookieControls", parameters.ToJsonString());
            preferenceErrors.Remove("Network.setCookieControls");
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        {
            preferenceErrors["Network.setCookieControls"] = error.Message;
            try
            {
                core.Profile.PreferredTrackingPreventionLevel = block
                    ? CoreWebView2TrackingPreventionLevel.Strict
                    : CoreWebView2TrackingPreventionLevel.Balanced;
            }
            catch (Exception fallback) when (fallback is COMException or InvalidOperationException)
            {
                preferenceErrors["PreferredTrackingPreventionLevel"] = fallback.Message;
            }
        }
    }

    // ---- IChromiumPrivacy ----

    public bool IsAvailable { get; }

    public int PrivateBrowserCount => attached.Count(entry => entry.IsPrivate);

    public bool HasPrivateProfile => PrivateBrowserCount > 0;

    public void Apply(PrivacySettings settings)
    {
        var previous = applied;
        applied = settings;
        if (previous is null || previous.BlockThirdPartyCookies != settings.BlockThirdPartyCookies)
        {
            foreach (var entry in attached.ToList()) _ = ApplyCookieControlsAsync(entry.Core);
        }
    }

    public bool NeedsRestart(PrivacySettings settings) =>
        environment is not null && startedWith is { } start && start.DnsOverHttps.WebView2Feature != settings.DnsOverHttps.WebView2Feature;

    public async Task<IReadOnlyList<ChromiumCookie>> CookiesAsync(bool privateProfile = false)
    {
        var core = privateProfile
            ? attached.FirstOrDefault(entry => entry.IsPrivate)?.Core
            : await DataBrowserAsync();
        if (core is null) return Array.Empty<ChromiumCookie>();
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("");
            return cookies.Select(cookie => new ChromiumCookie(cookie.Domain, cookie.Name, cookie.Path)).ToList();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            Trace.WriteLine($"[Privacy] Could not list Chromium's cookies: {error.Message}");
            return Array.Empty<ChromiumCookie>();
        }
    }

    public async Task ClearAsync(IReadOnlySet<BrowsingDataKind> kinds, DateTimeOffset? since)
    {
        if (await DataBrowserAsync() is not { } core) return;
        var flags = (CoreWebView2BrowsingDataKinds)0;
        foreach (var kind in kinds)
        {
            flags |= kind switch
            {
                BrowsingDataKind.History => CoreWebView2BrowsingDataKinds.BrowsingHistory,
                BrowsingDataKind.CookiesAndSiteData => CoreWebView2BrowsingDataKinds.AllSite,
                BrowsingDataKind.Cache => CoreWebView2BrowsingDataKinds.DiskCache,
                BrowsingDataKind.Downloads => CoreWebView2BrowsingDataKinds.DownloadHistory,
                BrowsingDataKind.FormData => CoreWebView2BrowsingDataKinds.GeneralAutofill,
                _ => 0,
            };
        }
        if (flags != 0) await ClearProfileAsync(core.Profile, flags, since);
    }

    private static async Task ClearProfileAsync(CoreWebView2Profile profile, CoreWebView2BrowsingDataKinds kinds, DateTimeOffset? since)
    {
        try
        {
            if (since is { } start) await profile.ClearBrowsingDataAsync(kinds, start.UtcDateTime, DateTime.UtcNow.AddMinutes(1));
            else await profile.ClearBrowsingDataAsync(kinds);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            Trace.WriteLine($"[Privacy] Chromium could not clear browsing data: {error.Message}");
        }
    }

    public async Task ClearSiteAsync(string site, IReadOnlyList<string> origins)
    {
        if (await DataBrowserAsync() is not { } core) return;
        try
        {
            var manager = core.CookieManager;
            foreach (var cookie in await manager.GetCookiesAsync(""))
            {
                var domain = cookie.Domain.TrimStart('.').ToLowerInvariant();
                if (domain == site || domain.EndsWith("." + site, StringComparison.Ordinal)) manager.DeleteCookie(cookie);
            }
            foreach (var origin in origins)
            {
                var parameters = new JsonObject { ["origin"] = origin, ["storageTypes"] = "all" };
                await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", parameters.ToJsonString());
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            Trace.WriteLine($"[Privacy] Chromium could not clear {site}: {error.Message}");
        }
    }

    public JsonObject Status()
    {
        var errors = new JsonObject();
        foreach (var (name, message) in preferenceErrors) errors[name] = message;
        return new JsonObject
        {
            ["environment"] = environment is { IsCompletedSuccessfully: true },
            ["startedWithDnsFeature"] = startedWith?.DnsOverHttps.WebView2Feature,
            ["browsers"] = attached.Count,
            ["privateBrowsers"] = PrivateBrowserCount,
            ["errors"] = errors,
            ["trackingPrevention"] = attached.FirstOrDefault(entry => !entry.IsPrivate)?.Core is { } core ? TryRead(() => core.Profile.PreferredTrackingPreventionLevel.ToString()) : null,
        };
    }

    private static string? TryRead(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A browser on the normal profile to run data operations through: any open tab's, or else a
    /// hidden one Mosaic creates (and keeps) for the purpose. Null when Chromium cannot start.
    /// </summary>
    private async Task<CoreWebView2?> DataBrowserAsync()
    {
        if (attached.FirstOrDefault(entry => !entry.IsPrivate) is { } open) return open.Core;
        if (dataController is not null) return dataController.CoreWebView2;
        if (!IsAvailable || environment is null || windowHandle() is var hwnd && hwnd == 0) return null;
        try
        {
            var shared = await environment;
            dataController = await shared.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
            dataController.IsVisible = false;
            return dataController.CoreWebView2;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            Trace.WriteLine($"[Privacy] Could not open Chromium's profile: {error.Message}");
            return null;
        }
    }
}
