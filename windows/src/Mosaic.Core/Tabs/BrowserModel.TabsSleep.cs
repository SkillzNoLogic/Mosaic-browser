using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;
using Mosaic.Core.Tabs;

namespace Mosaic.Core.Browser;

// Tabs area, part 4: sleeping tabs, unloading background tabs to free memory in both engines.
// Port of Sources/Mosaic/Features/Tabs/BrowserModel+Sleep.swift.
//
// - Chromium: the tab's WebView2 is closed; URL and title are kept and the page is loaded again
//   when the tab is selected. Back/forward history is lost.
// - Gecko: the agent discards the tab's browser (agent `discardTab`, gBrowser.discardBrowser);
//   Gecko keeps the tab, its history and scroll position, and restores it when Mosaic selects
//   it. If the agent cannot discard, the Gecko tab is closed instead and reopened from its URL
//   when selected.
//
// The policy (SleepPolicy.cs) runs every 30 seconds; Windows memory pressure is read every 10
// seconds (MemoryPressureMonitor) and a rise runs it at once. Exempt: the selected tab, tabs
// playing audio (Gecko via the agent's `tabActivity`; Chromium through IChromiumAudio), tabs the
// downloads area marks as downloading, pinned tabs if so configured, and (automatic unloading
// only) Gecko pages that ask to stay loaded (a beforeunload handler, an open dialog).
public sealed partial class BrowserModel
{
    private static readonly TimeSpan PressureInterval = TimeSpan.FromSeconds(10);
    private const int SleepCheckEvery = 3;

    private IMemoryPressureSource? memoryPressureSource;
    private CancellationTokenSource? sleepLoop;
    private bool isSleepCheckRunning;
    private MemoryPressure memoryPressure;
    private DateTimeOffset? lastSleepCheck;

    /// <summary>The last memory pressure read (or simulated by automation).</summary>
    public MemoryPressure MemoryPressure { get => memoryPressure; private set => Set(ref memoryPressure, value); }

    /// <summary>The last run of the sleeping-tabs policy, for the Performance page.</summary>
    public DateTimeOffset? LastSleepCheck { get => lastSleepCheck; private set => Set(ref lastSleepCheck, value); }

    public int AsleepTabCount => Tabs.Count(t => t.IsAsleep);

    private void StartSleepingTabs()
    {
        sleepLoop = new CancellationTokenSource();
        _ = SleepLoopAsync(sleepLoop.Token);
    }

    private async Task SleepLoopAsync(CancellationToken token)
    {
        var tick = 0;
        while (!token.IsCancellationRequested && !isShutDown)
        {
            try
            {
                await Task.Delay(PressureInterval, time, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (token.IsCancellationRequested || isShutDown) return;
            var level = memoryPressureSource?.Read() ?? MemoryPressure.Normal;
            var rose = level > memoryPressure;
            MemoryPressure = level;
            if (++tick % SleepCheckEvery == 0 || (rose && tabSettings.Sleep.RespondToMemoryPressure))
            {
                await RunSleepCheckAsync();
            }
        }
    }

    /// <summary>Also used by automation to simulate pressure.</summary>
    public void MemoryPressureChanged(MemoryPressure level)
    {
        MemoryPressure = level;
        if (level == MemoryPressure.Normal || !tabSettings.Sleep.RespondToMemoryPressure) return;
        _ = RunSleepCheckAsync(level);
    }

    /// <summary>Selecting a sleeping tab brings it back.</summary>
    private void WakeIfAsleep(BrowserTab tab)
    {
        if (!tab.IsAsleep) return;
        tab.IsAsleep = false;
        // Keeps the blank page Gecko shows while restoring out of the address bar.
        tab.LastLoadRequest = time.GetUtcNow();
        // A discarded Gecko tab: Gecko restores it when Select sends selectTab.
        if (tab is { Engine: EngineKind.Gecko, GeckoId: not null } && Gecko.Status == GeckoStatus.Running) return;
        if (!tab.IsStartPage && !tab.IsInternalPage) Load(tab.Url, tab);
    }

    /// <summary>
    /// Unloads <paramref name="tab"/> ("Unload Tab"). The selected tab is unloaded after selecting
    /// its neighbour. <paramref name="force"/> (manual unloading) discards Gecko pages even if they
    /// would rather stay loaded. Returns whether the tab is now asleep.
    /// </summary>
    public async Task<bool> UnloadAsync(BrowserTab tab, bool force = true)
    {
        if (!Tabs.Contains(tab) || tab.IsAsleep || tab.IsStartPage || tab.IsInternalPage) return false;
        if (ReferenceEquals(tab, selectedTab))
        {
            if (SuccessorAfterClosing(tab) is not { } successor) return false;
            Select(successor);
        }
        switch (tab.Engine)
        {
            case EngineKind.Chromium:
                if (tab.Chromium is { } browser)
                {
                    tab.Chromium = null;
                    browser.Close();
                }
                tab.CanGoBack = false;
                tab.CanGoForward = false;
                break;
            default:
                if (tab.GeckoId is { } id && Gecko.Status == GeckoStatus.Running)
                {
                    try
                    {
                        var result = await Gecko.CallAsync("discardTab", id, force);
                        if (!(result is JsonValue value && value.TryGetValue(out bool discarded) && discarded)) return false;
                    }
                    catch (Exception)
                    {
                        // An agent without discardTab, or a failure: close the Gecko tab; it is
                        // reopened from its URL when selected.
                        if (!Tabs.Contains(tab) || ReferenceEquals(tab, selectedTab)) return false;
                        DetachFromEngine(tab);
                    }
                }
                else
                {
                    // Nothing in Gecko yet (still opening): let go of the pending open.
                    tab.GeckoGeneration++;
                    tab.GeckoWork = null;
                }
                break;
        }
        // Closed, or selected again, while Gecko was discarding it.
        if (!Tabs.Contains(tab) || ReferenceEquals(tab, selectedTab)) return false;
        tab.IsAsleep = true;
        tab.IsLoading = false;
        tab.IsPlayingAudio = false;
        Notify(nameof(AsleepTabCount));
        return true;
    }

    /// <summary>Menu and palette entry point for <see cref="UnloadAsync"/>: explains when it cannot.</summary>
    public async Task UnloadTabAsync(BrowserTab tab)
    {
        if (await UnloadAsync(tab)) return;
        Notice = tab.IsAsleep ? "This tab is already unloaded."
            : VisibleTabs.Count < 2 && ReferenceEquals(tab, selectedTab) ? "The only tab of a workspace can’t be unloaded."
            : "This page can’t be unloaded right now.";
    }

    /// <summary>Unloads every background tab that is not exempt, however recently it was used.</summary>
    public async Task<int> UnloadBackgroundTabsAsync()
    {
        var settings = tabSettings.Sleep with { RespondToMemoryPressure = true };
        var unloaded = await RunSleepCheckAsync(MemoryPressure.Critical, settings, force: true);
        Notice = unloaded.Count == 0 ? "No tabs to unload." : $"Unloaded {unloaded.Count} {(unloaded.Count == 1 ? "tab" : "tabs")}.";
        return unloaded.Count;
    }

    /// <summary>
    /// The downloads area reports downloads started from a tab (by <see cref="BrowserTab.Id"/>);
    /// such tabs are not unloaded while the download runs.
    /// </summary>
    public void SetDownloadActive(bool active, Guid tabId)
    {
        if (active) downloadingTabs.Add(tabId);
        else downloadingTabs.Remove(tabId);
    }

    /// <summary>Applies the policy once. Returns the tabs it unloaded.</summary>
    public async Task<List<Guid>> RunSleepCheckAsync(MemoryPressure? pressure = null, SleepSettings? settings = null, bool force = false)
    {
        if (!tabsStarted || tabsFinished || isShutDown || isSleepCheckRunning) return [];
        isSleepCheckRunning = true;
        try
        {
            await RefreshGeckoActivityAsync();
            var now = time.GetUtcNow();
            var ids = SleepPolicy.TabsToUnload(
                Tabs.Select(SleepCandidateFor).ToList(), settings ?? tabSettings.Sleep, pressure ?? memoryPressure, now);
            var unloaded = new List<Guid>();
            foreach (var id in ids)
            {
                if (TabWithId(id) is { } tab && await UnloadAsync(tab, force)) unloaded.Add(id);
            }
            LastSleepCheck = now;
            return unloaded;
        }
        finally
        {
            isSleepCheckRunning = false;
        }
    }

    internal SleepCandidate SleepCandidateFor(BrowserTab tab) => new(
        tab.Id, tab.LastActivated,
        IsSelected: ReferenceEquals(tab, selectedTab),
        IsAsleep: tab.IsAsleep,
        HasLoadedContent: !tab.IsStartPage && !tab.IsInternalPage && (tab.Chromium is not null || tab.GeckoId is not null),
        IsPinned: tab.IsPinned,
        IsPlayingAudio: tab.IsPlayingAudio,
        HasActiveDownload: downloadingTabs.Contains(tab.Id));

    /// <summary>
    /// Reads which Gecko tabs play audio, and which Gecko unloaded or restored by itself (Firefox
    /// has its own low-memory tab unloader), so <see cref="BrowserTab.IsAsleep"/> stays true to Gecko.
    /// </summary>
    private async Task RefreshGeckoActivityAsync()
    {
        if (Gecko.Status != GeckoStatus.Running || !Tabs.Any(t => t is { Engine: EngineKind.Gecko, GeckoId: not null })) return;
        JsonNode? result;
        try
        {
            result = await Gecko.CallAsync("tabActivity");
        }
        catch (Exception)
        {
            return;
        }
        if (result is not JsonArray list) return;
        var byId = new Dictionary<int, JsonObject>();
        foreach (var entry in list.OfType<JsonObject>())
        {
            if (entry["id"] is JsonValue idValue && idValue.TryGetValue(out int id)) byId[id] = entry;
        }
        foreach (var tab in Tabs)
        {
            if (tab is not { Engine: EngineKind.Gecko, GeckoId: { } id } || !byId.TryGetValue(id, out var info)) continue;
            tab.IsPlayingAudio = info["audible"] is JsonValue audible && audible.TryGetValue(out bool playing) && playing;
            if (ReferenceEquals(tab, selectedTab) || info["discarded"] is not JsonValue discardedValue
                || !discardedValue.TryGetValue(out bool discarded)) continue;
            tab.IsAsleep = discarded;
        }
        Notify(nameof(AsleepTabCount));
    }
}
