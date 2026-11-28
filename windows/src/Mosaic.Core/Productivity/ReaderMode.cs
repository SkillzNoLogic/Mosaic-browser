using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Productivity;

/// <summary>
/// Reader view in both engines. Port of <c>Sources/Mosaic/Features/Productivity/ReaderMode.swift</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Gecko</b> has its own reader view (<c>about:reader?url=…</c>, with Firefox's toolbar for
/// font, size, width, spacing and colours). Mosaic turns it on and off through the agent
/// (<c>setReaderMode</c>, Firefox's own reader actor) and keeps Firefox's <c>reader.*</c>
/// preferences in step with <see cref="Settings"/>, both ways.</item>
/// <item><b>Chromium</b> has none, so Mosaic injects Mozilla's Readability 0.6.0 (shipped in
/// <c>reader\</c> next to the executable, from the repository's <c>Resources/reader</c>) to
/// extract the article and draws a reader page over the tab (<c>reader-view.js</c>) with the
/// same controls and scales. Leaving removes it; the page underneath is untouched.</item>
/// </list>
/// Each tab has <see cref="IsAvailable"/> (the page looks like an article) and
/// <see cref="IsActive"/>, for the toolbar's reader button. UI thread only.
/// </remarks>
public sealed class ReaderMode
{
    public const string GeckoReaderPrefix = "about:reader?";
    /// <summary>Prefix of the reader page's messages (<c>chrome.webview.postMessage</c>).</summary>
    internal const string MessagePrefix = "__mosaicReader:";

    private static readonly TimeSpan[] GeckoProbeDelays = [TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5)];
    private static readonly TimeSpan ChromiumProbeDelay = TimeSpan.FromMilliseconds(150);

    private sealed class TabState
    {
        public bool Available;
        public bool Active;
        public string? LastUrl;
        public EngineKind? LastEngine;
        /// <summary>Chromium: the page the reader overlay was drawn over.</summary>
        public string? ChromiumReaderUrl;
        public CancellationTokenSource? Probe;
        public IPageScriptHost? Listening;
        public Action<string>? Listener;
    }

    private readonly BrowserModel model;
    private readonly ProductivityFiles files;
    private readonly string resourcesDirectory;
    private readonly Dictionary<Guid, TabState> states = [];
    private readonly Dictionary<Guid, BrowserTab> watched = [];
    private ReaderSettings settings = new();
    private Task<ReaderScripts?>? scripts;
    private bool started;

    internal ReaderMode(BrowserModel model, ProductivityFiles files, string resourcesDirectory)
    {
        this.model = model;
        this.files = files;
        this.resourcesDirectory = resourcesDirectory;
    }

    /// <summary>Where the reader scripts are shipped: <c>reader\</c> next to the executable.</summary>
    public static string DefaultResourcesDirectory => Path.Combine(AppContext.BaseDirectory, "reader");

    public ReaderSettings Settings => settings;

    /// <summary>A tab's reader state or the settings changed (argument: the tab, or null for settings).</summary>
    public event Action<BrowserTab?>? Changed;

    public bool IsActive(BrowserTab tab) => states.TryGetValue(tab.Id, out var state) && state.Active;

    public bool IsAvailable(BrowserTab tab) => states.TryGetValue(tab.Id, out var state) && state.Available;

    internal async Task LoadAsync()
    {
        var text = await Task.Run(() => files.Read(ProductivityFiles.ReaderFile));
        if (text is not null) settings = ReaderSettings.FromJson(text);
    }

    internal void Start()
    {
        if (started) return;
        started = true;
        model.Tabs.CollectionChanged += TabsChanged;
        Watch();
    }

    // ---- Settings ----

    /// <summary>New settings from Mosaic's side (palette, toolbar, automation): saved, and applied to every open reader view.</summary>
    public void Update(ReaderSettings value)
    {
        if (!Store(value)) return;
        foreach (var tab in model.Tabs.ToList())
        {
            if (!IsActive(tab)) continue;
            if (tab.Engine == EngineKind.Chromium)
            {
                _ = PageScripting.For(tab.Chromium)?.ExecuteScriptAsync($"window.__mosaicReader && window.__mosaicReader.apply({settings.ToJson()})");
            }
            else if (tab.GeckoId is { } id && model.Gecko.Status == GeckoStatus.Running)
            {
                _ = ApplyGeckoPrefsAndReloadAsync(id);
            }
        }
    }

    private async Task ApplyGeckoPrefsAndReloadAsync(int id)
    {
        try
        {
            await model.Gecko.CallAsync("setReaderPrefs", settings.GeckoPreferences());
            // Gecko's reader reads its preferences when the page loads.
            model.Gecko.Send("reload", id);
        }
        catch (Exception) { }
    }

    private bool Store(ReaderSettings value)
    {
        if (value == settings) return false;
        settings = value;
        files.Write(ProductivityFiles.ReaderFile, value.ToJson());
        Changed?.Invoke(null);
        return true;
    }

    // ---- Turning it on and off ----

    public void Toggle(BrowserTab tab) => SetActive(!IsActive(tab), tab);

    /// <summary>Whether reader view can be switched on or off for <paramref name="tab"/> now.</summary>
    public bool CanToggle(BrowserTab? tab)
    {
        if (tab is null || tab.IsStartPage || tab.Failure is not null) return false;
        if (!ProductivityServices.IsWebPage(tab.Url) && !tab.Url.StartsWith(GeckoReaderPrefix, StringComparison.Ordinal)) return false;
        return tab.Engine == EngineKind.Gecko ? tab.GeckoId is not null : PageScripting.For(tab.Chromium)?.CanRunScripts == true;
    }

    public void SetActive(bool on, BrowserTab tab)
    {
        if (IsActive(tab) == on) return;
        var isReaderPage = tab.Url.StartsWith(GeckoReaderPrefix, StringComparison.Ordinal);
        if (tab.IsStartPage || tab.Failure is not null || (!ProductivityServices.IsWebPage(tab.Url) && !isReaderPage))
        {
            if (on) model.Notice = "Reader view is for web pages.";
            return;
        }
        if (tab.Engine == EngineKind.Chromium)
        {
            if (on) _ = EnterChromiumAsync(tab);
            else LeaveChromium(tab);
        }
        else
        {
            _ = SetGeckoAsync(on, tab);
        }
    }

    // ---- Chromium ----

    private Task<ReaderScripts?> Scripts() => scripts ??= Task.Run(() => ReaderScripts.Load(resourcesDirectory));

    private async Task EnterChromiumAsync(BrowserTab tab)
    {
        var host = PageScripting.For(tab.Chromium);
        if (host is null || !host.CanRunScripts)
        {
            model.Notice = "The page is still loading.";
            return;
        }
        if (await Scripts() is not { } loaded)
        {
            model.Notice = "Reader view is missing from this build (reader folder).";
            return;
        }
        var url = tab.Url;
        Listen(tab, host);
        var result = await host.ExecuteScriptAsync(loaded.Enter(settings));
        if (!model.Tabs.Contains(tab) || !ReferenceEquals(PageScripting.For(tab.Chromium), host)) return;
        if (ReaderScripts.IsOk(result))
        {
            var state = State(tab);
            state.ChromiumReaderUrl = url;
            state.Active = true;
            state.Available = true;
            Changed?.Invoke(tab);
        }
        else
        {
            StopListening(tab);
            model.Notice = result is null ? "Reader view could not read this page." : "Reader view isn’t available for this page.";
        }
    }

    private void LeaveChromium(BrowserTab tab)
    {
        var state = State(tab);
        state.ChromiumReaderUrl = null;
        state.Active = false;
        _ = PageScripting.For(tab.Chromium)?.ExecuteScriptAsync("window.__mosaicReader ? window.__mosaicReader.exit() : false");
        StopListening(tab);
        Changed?.Invoke(tab);
    }

    private void Listen(BrowserTab tab, IPageScriptHost host)
    {
        var state = State(tab);
        if (ReferenceEquals(state.Listening, host)) return;
        StopListening(tab);
        Action<string> listener = message => HandleMessage(message, tab);
        host.MessageReceived += listener;
        state.Listening = host;
        state.Listener = listener;
    }

    private void StopListening(BrowserTab tab)
    {
        if (!states.TryGetValue(tab.Id, out var state) || state.Listening is not { } host) return;
        host.MessageReceived -= state.Listener;
        state.Listening = null;
        state.Listener = null;
    }

    /// <summary>Messages from the reader page: its toolbar changed a setting, or it was closed.</summary>
    internal void HandleMessage(string message, BrowserTab tab)
    {
        if (!message.StartsWith(MessagePrefix, StringComparison.Ordinal)) return;
        JsonObject? payload;
        try { payload = JsonNode.Parse(message[MessagePrefix.Length..]) as JsonObject; }
        catch (JsonException) { return; }
        var type = payload?["type"] is JsonValue kind && kind.TryGetValue<string>(out var text) ? text : null;
        switch (type)
        {
            case "settings":
                if (payload!["settings"] is not JsonObject raw) return;
                if (!Store(ReaderSettings.FromJsonObject(raw))) return;
                // Other open reader views follow.
                foreach (var other in model.Tabs)
                {
                    if (ReferenceEquals(other, tab) || !IsActive(other) || other.Engine != EngineKind.Chromium) continue;
                    _ = PageScripting.For(other.Chromium)?.ExecuteScriptAsync($"window.__mosaicReader && window.__mosaicReader.apply({settings.ToJson()})");
                }
                break;
            case "closed":
                var state = State(tab);
                state.ChromiumReaderUrl = null;
                state.Active = false;
                StopListening(tab);
                Changed?.Invoke(tab);
                break;
        }
    }

    // ---- Gecko ----

    private async Task SetGeckoAsync(bool on, BrowserTab tab)
    {
        if (tab.GeckoId is not { } id || model.Gecko.Status != GeckoStatus.Running)
        {
            model.Notice = "The page is still loading.";
            return;
        }
        try
        {
            if (on) await model.Gecko.CallAsync("setReaderPrefs", settings.GeckoPreferences());
            else await PullGeckoPrefsAsync();
            await model.Gecko.CallAsync("setReaderMode", id, on);
        }
        catch (Exception error)
        {
            model.Notice = error.Message;
        }
    }

    /// <summary>Takes over what the user changed in Gecko's own reader toolbar.</summary>
    private async Task PullGeckoPrefsAsync()
    {
        if (model.Gecko.Status != GeckoStatus.Running) return;
        try
        {
            if (await model.Gecko.CallAsync("readerPrefs") is JsonObject prefs) Store(settings.MergingGeckoPreferences(prefs));
        }
        catch (Exception) { }
    }

    // ---- Watching tabs ----

    private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Watch();

    private void Watch()
    {
        var current = model.Tabs.ToDictionary(tab => tab.Id);
        foreach (var (id, tab) in watched.ToList())
        {
            if (current.ContainsKey(id)) continue;
            tab.PropertyChanged -= TabPropertyChanged;
            watched.Remove(id);
            if (states.Remove(id, out var state))
            {
                state.Probe?.Cancel();
                if (state.Listening is { } host) host.MessageReceived -= state.Listener;
            }
        }
        foreach (var (id, tab) in current)
        {
            if (watched.ContainsKey(id)) continue;
            watched[id] = tab;
            tab.PropertyChanged += TabPropertyChanged;
            PageChanged(tab);
        }
    }

    private void TabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BrowserTab tab) return;
        if (e.PropertyName is nameof(BrowserTab.Url) or nameof(BrowserTab.IsLoading) or nameof(BrowserTab.Engine)
            or nameof(BrowserTab.GeckoId) or nameof(BrowserTab.Chromium))
        {
            PageChanged(tab);
        }
    }

    private TabState State(BrowserTab tab)
    {
        if (!states.TryGetValue(tab.Id, out var state)) states[tab.Id] = state = new TabState();
        return state;
    }

    internal void PageChanged(BrowserTab tab)
    {
        var state = State(tab);
        var url = tab.Url;
        var urlChanged = state.LastUrl != url;
        var engineChanged = state.LastEngine is { } last && last != tab.Engine;
        state.LastUrl = url;
        state.LastEngine = tab.Engine;
        var before = (state.Active, state.Available);

        // A tab moved to Chromium while showing Gecko's reader view: Chromium cannot load
        // about:reader, so open the article itself there.
        if (tab.Engine == EngineKind.Chromium && url.StartsWith(GeckoReaderPrefix, StringComparison.Ordinal))
        {
            state.Active = false;
            state.Available = false;
            if (OriginalUrl(url) is { } original) model.Load(original, tab);
            Changed?.Invoke(tab);
            return;
        }

        if (tab.Engine == EngineKind.Chromium)
        {
            if (engineChanged || (state.ChromiumReaderUrl is { } readerUrl && (readerUrl != url || tab.IsLoading)))
            {
                // A new document replaced the one the reader overlay was drawn on.
                state.ChromiumReaderUrl = null;
                StopListening(tab);
                state.Active = false;
            }
        }
        else
        {
            var active = url.StartsWith(GeckoReaderPrefix, StringComparison.Ordinal);
            if (state.Active && !active) _ = PullGeckoPrefsAsync();
            if (engineChanged) StopListening(tab);
            state.Active = active;
        }

        if (urlChanged || engineChanged)
        {
            state.Probe?.Cancel();
            state.Probe = null;
            state.Available = tab.Engine == EngineKind.Gecko && state.Active;
        }
        if (!tab.IsLoading && state.Probe is null && !state.Available && ProductivityServices.IsWebPage(url))
        {
            var cancel = new CancellationTokenSource();
            state.Probe = cancel;
            _ = ProbeAsync(tab, url, state, cancel.Token);
        }
        if (before != (state.Active, state.Available)) Changed?.Invoke(tab);
    }

    /// <summary>The address an <c>about:reader</c> page shows.</summary>
    public static string? OriginalUrl(string readerUrl)
    {
        if (!readerUrl.StartsWith(GeckoReaderPrefix, StringComparison.Ordinal)) return null;
        foreach (var part in readerUrl[GeckoReaderPrefix.Length..].Split('&'))
        {
            if (part.StartsWith("url=", StringComparison.Ordinal)) return Uri.UnescapeDataString(part[4..].Replace('+', ' '));
        }
        return null;
    }

    /// <summary>Asks the engine whether the page looks like an article.</summary>
    private async Task ProbeAsync(BrowserTab tab, string url, TabState state, CancellationToken cancel)
    {
        try
        {
            if (tab.Engine == EngineKind.Chromium)
            {
                await Task.Delay(ChromiumProbeDelay, model.ProductivityTime, cancel);
                var host = PageScripting.For(tab.Chromium);
                if (host is null || !host.CanRunScripts || tab.Url != url || await Scripts() is not { } loaded)
                {
                    // Not ready yet: let the next change (load finished) try again.
                    if (state.Probe is { } probe && probe.Token == cancel) state.Probe = null;
                    return;
                }
                var result = await host.ExecuteScriptAsync(loaded.Probe);
                if (cancel.IsCancellationRequested || tab.Url != url) return;
                SetAvailable(tab, state, result == "true");
                return;
            }
            // Gecko judges the page shortly after it loads; ask a few times.
            foreach (var delay in GeckoProbeDelays)
            {
                await Task.Delay(delay, model.ProductivityTime, cancel);
                if (tab.Url != url || tab.GeckoId is not { } id || model.Gecko.Status != GeckoStatus.Running) return;
                var info = await model.Gecko.CallAsync("readerInfo", id) as JsonObject;
                if (cancel.IsCancellationRequested || tab.Url != url) return;
                if (info?["available"] is JsonValue available && available.TryGetValue<bool>(out var yes) && yes)
                {
                    SetAvailable(tab, state, true);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* an engine that went away: the next page change probes again */ }
    }

    private void SetAvailable(BrowserTab tab, TabState state, bool available)
    {
        if (state.Available == available) return;
        state.Available = available;
        Changed?.Invoke(tab);
    }

    /// <summary>Before the browsers close at quit: no page-script channel may outlive its browser.</summary>
    internal void Shutdown()
    {
        foreach (var tab in model.Tabs) StopListening(tab);
        foreach (var state in states.Values) state.Probe?.Cancel();
    }

    // ---- Automation ----

    public JsonObject AutomationState()
    {
        var tabs = new JsonArray();
        foreach (var tab in model.Tabs)
        {
            tabs.Add(new JsonObject
            {
                ["available"] = IsAvailable(tab),
                ["active"] = IsActive(tab),
                ["engine"] = tab.Engine.Id(),
                ["url"] = tab.Url,
            });
        }
        return new JsonObject { ["settings"] = settings.ToJsonObject(), ["tabs"] = tabs };
    }
}

/// <summary>Readability, its "readerable" check and the reader page, each wrapped so nothing leaks into the page's globals.</summary>
internal sealed record ReaderScripts(string Probe, string Bootstrap)
{
    public const string ReadabilityFile = "Readability.js";
    public const string ReaderableFile = "Readability-readerable.js";
    public const string ViewFile = "reader-view.js";

    public static ReaderScripts? Load(string directory)
    {
        try
        {
            var readability = File.ReadAllText(Path.Combine(directory, ReadabilityFile));
            var readerable = File.ReadAllText(Path.Combine(directory, ReaderableFile));
            var view = File.ReadAllText(Path.Combine(directory, ViewFile));
            return new ReaderScripts(
                $"(function () {{ var module;\n{readerable}\nreturn isProbablyReaderable(document); }})()",
                $"(function () {{ var module;\n{readability}\n{view}\n}})();");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Installs the message function the reader page reports through (WebView2's
    /// <c>chrome.webview.postMessage</c>, prefixed so other areas' messages are not confused
    /// with it), defines the reader and enters it.
    /// </summary>
    public string Enter(ReaderSettings settings) =>
        "window.__mosaicReaderPost = function (message) { try { window.chrome.webview.postMessage(" +
        JsonSerializer.Serialize(ReaderMode.MessagePrefix) + " + message); } catch (_) {} };\n" +
        Bootstrap + "\nwindow.__mosaicReader.enter(" + settings.ToJson() + ");";

    /// <summary>Whether <c>enter</c>'s result (JSON) says it worked.</summary>
    public static bool IsOk(string? result)
    {
        if (result is null) return false;
        try
        {
            return JsonNode.Parse(result) is JsonObject value && value["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var yes) && yes;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
