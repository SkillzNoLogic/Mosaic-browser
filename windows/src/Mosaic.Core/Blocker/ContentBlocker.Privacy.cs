using System.Diagnostics;
using Mosaic.Core.Blocker;

namespace Mosaic.Core.Browser;

// Privacy area: the content blocker's features beyond v0.1's network filtering, ported from
// Sources/Mosaic/Model/ContentBlocker.swift. Cosmetic filtering (Chromium: RequestFilter builds
// each frame's style script; Gecko: the agent's configurePrivacy/setCosmeticOptions), the optional
// filter lists, the user's own filters ("My filters") with validation, and the per-page dashboard.
// The rest of the class (engine builds, list updates, the switches) is in Browser/ContentBlocker.cs.

/// <summary>One blocked request, for the blocker dashboard.</summary>
/// <param name="Url">The request.</param>
/// <param name="Type">Its webRequest resource type (script, image, …).</param>
/// <param name="Count">How many times the page asked for it.</param>
/// <param name="Filter">The filter that matched, as uBO's logger writes it.</param>
/// <param name="List">The list the filter comes from, when it could be found.</param>
public sealed record BlockedRequest(string Url, string Type, int Count, string Filter, string? List)
{
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 ? parsed.Host : Url;
}

public sealed partial class ContentBlocker
{
    private BlockerOptions? blockerOptions;
    private string? userFilters;

    /// <summary>Where <see cref="BlockerOptions"/> are kept: beside the lists folder.</summary>
    private string OptionsPath => BlockerOptions.PathFor(options.ListsDirectory);

    /// <summary>Cosmetic filtering and the chosen lists; read once, on first use.</summary>
    public BlockerOptions Options
    {
        get
        {
            if (blockerOptions is null)
            {
                blockerOptions = BlockerOptions.Load(OptionsPath);
                Filter.CosmeticsEnabled = blockerOptions.CosmeticEnabled;
            }
            return blockerOptions;
        }
        private set
        {
            if (Equals(blockerOptions, value)) return;
            blockerOptions = value;
            value.Save(OptionsPath);
            Filter.CosmeticsEnabled = value.CosmeticEnabled;
            Notify(nameof(Options));
            Notify(nameof(CosmeticEnabled));
            Notify(nameof(EnabledListIds));
        }
    }

    /// <summary>Hide page elements named by cosmetic filters (<c>##.ad</c>).</summary>
    public bool CosmeticEnabled => Options.CosmeticEnabled;

    /// <summary>IDs of the filter lists in use (see <see cref="FilterListCatalog.All"/>).</summary>
    public IReadOnlySet<string> EnabledListIds => Options.EnabledListIds;

    /// <summary>Cosmetic filters compiled into the engine Chromium tabs use; zeros until it has loaded.</summary>
    public CosmeticStats CosmeticStats => Filter.Engine?.CosmeticStats ?? new CosmeticStats(0, 0, 0);

    /// <summary>The user's own filters ("My filters"); read from disk on first use.</summary>
    public string UserFilters => userFilters ??= store.UserFilters();

    /// <summary>Result of the last list update in this session, for the settings page.</summary>
    public string? LastUpdateMessage => store.LastUpdate?.Message;

    /// <summary>The lists on disk, for the settings page and the dashboard.</summary>
    public FilterListStore Store => store;

    /// <summary>Whether <see cref="Validate"/> can check filters yet (the engine has loaded).</summary>
    public bool CanValidate => Filter.Engine?.SupportsValidation == true;

    /// <summary>
    /// The privacy area's start-up work, after <see cref="Start"/>: applies the saved cosmetic
    /// switch and fetches lists that are turned on but were never downloaded (optional lists are
    /// not shipped). Cheap when there is nothing to fetch.
    /// </summary>
    public void StartPrivacy()
    {
        _ = Options;
        if (status == BlockerStatus.Unavailable || isShutDown) return;
        if (store.MissingLists().Count > 0) _ = DownloadMissingListsAsync();
    }

    /// <summary>Downloads enabled lists that are not on disk yet, then rebuilds both engines.</summary>
    public async Task DownloadMissingListsAsync()
    {
        var missing = store.MissingLists().Select(list => list.Id).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0 || isUpdating || isShutDown) return;
        IsUpdating = true;
        try
        {
            var result = await store.UpdateAsync(options.HttpHandler, only: missing);
            Notify(nameof(LastUpdateMessage));
            if (result.Updated.Count == 0 || isShutDown) return;
            ListsDate = store.OldestModification();
            await RebuildBothEnginesAsync();
        }
        finally
        {
            IsUpdating = false;
        }
    }

    /// <summary>Recompiles the lists on disk (and the user's filters) into both engines.</summary>
    public async Task RebuildBothEnginesAsync()
    {
        if (status == BlockerStatus.Unavailable || isShutDown) return;
        RebuildEngine();
        await ConfigureGeckoAsync();
    }

    /// <summary>The engine build in progress (or the last one); the automation waits on it.</summary>
    public Task CurrentBuild => Building;

    public void SetCosmeticEnabled(bool enabled)
    {
        if (Options.CosmeticEnabled == enabled) return;
        Options = Options with { CosmeticEnabled = enabled };
        gecko.Send("setCosmeticOptions", enabled);
    }

    /// <summary>Turns a filter list on or off. A list never downloaded is fetched first.</summary>
    public void SetList(FilterList list, bool enabled)
    {
        if (Options.EnabledListIds.Contains(list.Id) == enabled) return;
        Options = Options.WithList(list.Id, enabled);
        store.UseLists(Options.EnabledLists);
        ListsDate = store.OldestModification();
        if (status == BlockerStatus.Unavailable) return;
        _ = enabled && !store.IsDownloaded(list) ? DownloadMissingListsAsync() : RebuildBothEnginesAsync();
    }

    public bool IsListDownloaded(FilterList list) => store.IsDownloaded(list);

    /// <summary>Removes a site from the allowlist (Settings), or adds one.</summary>
    public void SetAllowlisted(bool allowlisted, string site)
    {
        var updated = settings.WithAllowlisted(allowlisted, site);
        if (ReferenceEquals(updated, settings)) return;
        Settings = updated;
        SettingsChanged();
    }

    // ---- My filters ----

    /// <summary>Checks filters with uBO's parser. Empty until the engine has loaded.</summary>
    public IReadOnlyList<FilterCheck> Validate(string filters) => Filter.Engine?.Validate(filters) ?? [];

    /// <summary>Saves the user's filters and rebuilds both engines with them.</summary>
    /// <exception cref="IOException">The file cannot be written (the message is presentable).</exception>
    /// <exception cref="UnauthorizedAccessException">As above, for want of permission.</exception>
    public void SaveUserFilters(string text)
    {
        store.SaveUserFilters(text);
        userFilters = text;
        Notify(nameof(UserFilters));
        if (status != BlockerStatus.Unavailable) _ = RebuildBothEnginesAsync();
    }

    // ---- Dashboard ----

    /// <summary>Requests blocked on the tab's current page, with the filter behind each.</summary>
    public async Task<IReadOnlyList<BlockedRequest>> BlockedRequestsAsync(BrowserTab tab)
    {
        List<(string Url, string Type, int Count, string Filter)> found = [];
        if (tab.Engine == EngineKind.Chromium)
        {
            var entries = Filter.Log.EntriesFor(tab.Url);
            var engine = Filter.Engine;
            found = await Task.Run(() => entries.Select(entry =>
                (entry.Url, entry.Type, entry.Count, engine?.Details(entry.Url, entry.DocumentUrl, entry.Type).Filter ?? "")).ToList());
        }
        else if (tab.GeckoId is { } id && gecko.Status == Gecko.GeckoStatus.Running)
        {
            try
            {
                if (await gecko.CallAsync("blockedRequests", id) is System.Text.Json.Nodes.JsonArray records)
                {
                    foreach (var record in records.OfType<System.Text.Json.Nodes.JsonObject>())
                    {
                        found.Add((Text(record["url"]), Text(record["type"]),
                            record["count"] is System.Text.Json.Nodes.JsonValue count && count.TryGetValue(out int number) ? number : 1,
                            Text(record["filter"])));
                    }
                }
            }
            catch (Exception error)
            {
                Trace.WriteLine($"[Blocker] Gecko's blocked requests could not be read: {error.Message}");
            }
        }
        var lists = store;
        return await Task.Run(() =>
        {
            var listByFilter = new Dictionary<string, string?>(StringComparer.Ordinal);
            return (IReadOnlyList<BlockedRequest>)[.. found.Select(item =>
            {
                if (!listByFilter.TryGetValue(item.Filter, out var list)) listByFilter[item.Filter] = list = lists.ListContaining(item.Filter);
                return new BlockedRequest(item.Url, item.Type, item.Count, item.Filter, list);
            })];
        });
    }

    private static string Text(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out string? text) ? text : "";
}
