using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Mosaic.Core.Tabs;

namespace Mosaic.Core.Browser;

/// <summary>"Switch to the tab that already shows this page?"</summary>
/// <remarks><c>TabId</c>: The tab that just navigated to the page.</remarks>
/// <remarks><c>ExistingTabId</c>: The tab that already showed it.</remarks>
/// <remarks><c>WorkspaceName</c>: Name of the existing tab's workspace when it is not the current one.</remarks>
/// <remarks><c>ClosesNewTab</c>: The new tab was a fresh start-page tab, so switching closes it.</remarks>
public sealed record DuplicateOffer(Guid Id, Guid TabId, Guid ExistingTabId, string Title, string? WorkspaceName, bool ClosesNewTab)
{
    public string Message => $"“{Title}” is already open {(WorkspaceName is { } name ? $"in {name}" : "in another tab")}.";
}

/// <summary>What a tab strip shows, in order, for the current workspace (see <see cref="BrowserModel.TabStrip"/>).</summary>
public enum TabStripEntryKind { Tab, GroupHeader }

/// <remarks><c>WorkspaceIndex</c>: For a tab: its index among the workspace's tabs (what <see cref="BrowserModel.MoveTab(BrowserTab, int)"/> takes).</remarks>
/// <remarks><c>IsHidden</c>: A member of a collapsed group: the strip leaves it out (or shows it folded).</remarks>
public sealed record TabStripEntry(TabStripEntryKind Kind, BrowserTab? Tab, TabGroup? Group, int WorkspaceIndex, bool IsHidden);

// Tabs area, part 1: state, the hooks BrowserModel.cs calls ("// tabs:" lines), queries and
// placement. Port of Sources/Mosaic/Features/Tabs/BrowserModel+Organization.swift (queries and
// placement) and TabsFeature.swift (state).
//
// `Tabs` holds every tab of every workspace, in tab-strip order; `VisibleTabs` is the selected
// workspace's, which is what a tab strip or sidebar shows. The ordering rules are pure and
// unit-tested in TabOrdering.cs; this file applies them to the live tabs.
public sealed partial class BrowserModel
{
    private static readonly TimeSpan PinnedCloseWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DuplicateOfferDuration = TimeSpan.FromSeconds(10);

    private bool tabsWired;
    private Guid selectedWorkspaceId;
    private BrowserTab? lastTabsSelection;
    private readonly Dictionary<Guid, Guid> selectionByWorkspace = [];
    /// <summary>While set, closed tabs are collected into one Recently Closed entry (group/workspace).</summary>
    private ClosedItem? closingBatch;
    private readonly List<ClosedTabRecord> closingRecords = [];
    private (Guid TabId, DateTimeOffset At)? pinnedCloseRequest;
    private readonly HashSet<Guid> downloadingTabs = [];
    /// <summary>The tab a link opened from, while <see cref="OpenLinkedTab"/> inserts its new tab.</summary>
    private BrowserTab? linkSource;
    private DuplicateOffer? duplicateOffer;
    private bool isTabSearchVisible;
    private string tabSearchQuery = "";
    private TabSettings tabSettings = new();

    /// <summary>The workspaces, in switcher order. Never empty.</summary>
    public ObservableCollection<Workspace> Workspaces { get; } = [new Workspace(Guid.NewGuid(), "Personal", "person")];

    /// <summary>Tab groups of every workspace.</summary>
    public ObservableCollection<TabGroup> Groups { get; } = [];

    /// <summary>The selected workspace's tabs, in order (pinned first, groups contiguous); kept in step with <see cref="BrowserModel.Tabs"/>.</summary>
    public ObservableCollection<BrowserTab> VisibleTabs { get; } = [];

    /// <summary>
    /// Raised after anything the tab strips draw changed: order, pins, groups (and their names,
    /// colours, collapsed state), workspaces, the selected workspace. Strips that draw group
    /// headers or a pinned grid rebuild from <see cref="TabStrip"/> here.
    /// </summary>
    public event Action? TabOrganizationChanged;

    /// <summary>
    /// Says whether a tab is private. Set by the privacy area (<c>tab =&gt; tab.IsPrivate</c>);
    /// private tabs never enter Recently Closed or the saved session.
    /// </summary>
    public Func<BrowserTab, bool> IsPrivateTab { get; set; } = _ => false;

    public Guid SelectedWorkspaceId
    {
        get => Workspaces.Any(w => w.Id == selectedWorkspaceId) ? selectedWorkspaceId : Workspaces[0].Id;
        private set
        {
            var before = SelectedWorkspaceId;
            selectedWorkspaceId = value;
            if (SelectedWorkspaceId == before) return;
            Notify(nameof(SelectedWorkspaceId));
            Notify(nameof(SelectedWorkspace));
            OrganizationChanged();
        }
    }

    /// <summary>The workspace on screen.</summary>
    public Workspace SelectedWorkspace => Workspaces.FirstOrDefault(w => w.Id == selectedWorkspaceId) ?? Workspaces[0];

    /// <summary>The settings of the Tabs &amp; Workspaces and Performance pages. Setting saves them.</summary>
    public TabSettings TabSettings
    {
        get => tabSettings;
        set
        {
            var old = tabSettings;
            if (value is null || !Set(ref tabSettings, value)) return;
            if (tabSettingsStore is { } store) _ = Task.Run(() => store.Save(value));
            TabSettingsChanged(old);
        }
    }

    public DuplicateOffer? DuplicateOffer
    {
        get => duplicateOffer;
        private set
        {
            if (Set(ref duplicateOffer, value)) Notify(nameof(HasDuplicateOffer));
        }
    }

    public bool HasDuplicateOffer => duplicateOffer is not null;

    /// <summary>The tab search panel (Ctrl+Shift+A) is showing.</summary>
    public bool IsTabSearchVisible { get => isTabSearchVisible; set => Set(ref isTabSearchVisible, value); }

    /// <summary>What automation last typed into tab search (<c>tabSearchQuery</c>), for <c>dump</c>.</summary>
    public string TabSearchQuery { get => tabSearchQuery; set => Set(ref tabSearchQuery, value ?? ""); }

    /// <summary>The model's clock (a test clock in unit tests).</summary>
    internal DateTimeOffset Now => time.GetUtcNow();

    // ---- Queries ----

    /// <summary>The workspace a tab belongs to (the first workspace for a tab that somehow lacks one).</summary>
    public Guid WorkspaceIdOf(BrowserTab tab) =>
        tab.WorkspaceId is { } id && Workspaces.Any(w => w.Id == id) ? id : Workspaces[0].Id;

    public Workspace? WorkspaceOf(BrowserTab tab)
    {
        var id = WorkspaceIdOf(tab);
        return Workspaces.FirstOrDefault(w => w.Id == id);
    }

    /// <summary>A workspace's tabs in tab-strip order.</summary>
    public List<BrowserTab> TabsIn(Guid workspaceId) => Tabs.Where(t => WorkspaceIdOf(t) == workspaceId).ToList();

    public IEnumerable<BrowserTab> PinnedTabs => VisibleTabs.Where(t => t.IsPinned);

    public IEnumerable<BrowserTab> UnpinnedTabs => VisibleTabs.Where(t => !t.IsPinned);

    /// <summary>Visible tabs that keyboard navigation visits: members of collapsed groups are skipped (except the selected tab).</summary>
    public List<BrowserTab> NavigableTabs
    {
        get
        {
            var collapsed = Groups.Where(g => g.IsCollapsed).Select(g => g.Id).ToHashSet();
            return TabsIn(SelectedWorkspaceId)
                .Where(t => ReferenceEquals(t, selectedTab) || t.GroupId is not { } g || !collapsed.Contains(g))
                .ToList();
        }
    }

    public TabGroup? GroupWithId(Guid? id) => id is { } value ? Groups.FirstOrDefault(g => g.Id == value) : null;

    public TabGroup? GroupOf(BrowserTab tab) => GroupWithId(tab.GroupId);

    public List<BrowserTab> TabsInGroup(Guid id) => Tabs.Where(t => t.GroupId == id).ToList();

    public BrowserTab? TabWithId(Guid id) => Tabs.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// The current workspace's strip: pinned tabs, then loose tabs and groups (a header entry
    /// before each group's members) in order. Members of a collapsed group are marked hidden.
    /// </summary>
    public List<TabStripEntry> TabStrip()
    {
        var result = new List<TabStripEntry>();
        var tabs = TabsIn(SelectedWorkspaceId);
        Guid? currentGroup = null;
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            var group = tab.IsPinned ? null : GroupOf(tab);
            if (group is not null && group.Id != currentGroup) result.Add(new(TabStripEntryKind.GroupHeader, null, group, i, false));
            currentGroup = group?.Id;
            result.Add(new(TabStripEntryKind.Tab, tab, group, i, group is { IsCollapsed: true }));
        }
        return result;
    }

    /// <summary>Tabs, of every workspace, the downloads area reports as downloading.</summary>
    public bool HasActiveDownload(BrowserTab tab) => downloadingTabs.Contains(tab.Id);

    // ---- Hooks called from BrowserModel.cs ----

    /// <summary>
    /// Inserts a new tab according to the ordering rules: after the selected tab, never among
    /// pinned tabs; a tab opened from a link (<see cref="OpenLinkedTab"/>, or one Gecko opened
    /// itself) goes right after its opener and into its group. Always handles the insertion.
    /// </summary>
    private bool InsertTab(BrowserTab tab)
    {
        EnsureTabsWired();
        var anchor = linkSource ?? selectedTab;
        var linked = linkSource is not null || tab.GeckoId is not null;
        tab.WorkspaceId ??= linkSource is { } source ? WorkspaceIdOf(source) : SelectedWorkspaceId;
        tab.LastActivated = time.GetUtcNow();
        var placement = TabOrdering.Insertion(TabSlots(), WorkspaceIdOf(tab), anchor?.Id, linked);
        tab.GroupId = placement.GroupId;
        Tabs.Insert(Math.Clamp(placement.Index, 0, Tabs.Count), tab);
        OrganizationChanged();
        return true;
    }

    /// <summary>A page asked for a new tab (target=_blank, window.open): open it next to the page, in its group.</summary>
    private void OpenLinkedTab(string url, BrowserTab source)
    {
        linkSource = source;
        try
        {
            var inCurrentWorkspace = WorkspaceIdOf(source) == SelectedWorkspaceId;
            NewTab(url, source.Engine, select: inCurrentWorkspace);
        }
        finally
        {
            linkSource = null;
        }
    }

    /// <summary>Bookkeeping whenever a tab becomes the selected one, by Mosaic or by Gecko.</summary>
    private void TabSelected(BrowserTab tab)
    {
        EnsureTabsWired();
        var now = time.GetUtcNow();
        if (lastTabsSelection is { } previous && !ReferenceEquals(previous, tab)) previous.LastActivated = now;
        lastTabsSelection = tab;
        tab.LastActivated = now;
        var workspace = WorkspaceIdOf(tab);
        tab.WorkspaceId ??= workspace;
        selectionByWorkspace[workspace] = tab.Id;
        if (GroupOf(tab) is { IsCollapsed: true } group)
        {
            group.IsCollapsed = false;
            TabOrganizationChanged?.Invoke();
        }
        if (pinnedCloseRequest is { } request && request.TabId != tab.Id) pinnedCloseRequest = null;
        SelectedWorkspaceId = workspace;
        WakeIfAsleep(tab);
        ScheduleSessionSave();
    }

    /// <summary>
    /// Called as <paramref name="tab"/> is about to close: records it in Recently Closed and
    /// returns the tab to select instead when it is the selected one (its right-hand neighbour in
    /// the workspace, else the left one; null when it is the workspace's last tab).
    /// </summary>
    private BrowserTab? TabClosing(BrowserTab tab)
    {
        RecordClosed(tab);
        return SuccessorAfterClosing(tab);
    }

    /// <summary>The tab to select when the selected <paramref name="tab"/> closes or leaves.</summary>
    private BrowserTab? SuccessorAfterClosing(BrowserTab tab)
    {
        if (!ReferenceEquals(tab, selectedTab)) return null;
        var order = NavigableTabs;
        var index = order.IndexOf(tab);
        if (index >= 0)
        {
            if (index + 1 < order.Count) return order[index + 1];
            if (index > 0) return order[index - 1];
        }
        return TabsIn(WorkspaceIdOf(tab)).FirstOrDefault(t => !ReferenceEquals(t, tab));
    }

    /// <summary>Cleanup after <paramref name="tab"/> left the list.</summary>
    private void TabRemoved(BrowserTab tab)
    {
        foreach (var (workspace, id) in selectionByWorkspace.ToList())
        {
            if (id == tab.Id) selectionByWorkspace.Remove(workspace);
        }
        if (duplicateOffer is { } offer && (offer.TabId == tab.Id || offer.ExistingTabId == tab.Id)) DuplicateOffer = null;
        downloadingTabs.Remove(tab.Id);
        if (ReferenceEquals(lastTabsSelection, tab)) lastTabsSelection = null;
        PruneEmptyGroups();
        OrganizationChanged();
    }

    /// <summary>Ctrl+W protection for pinned tabs: the first press only explains, a second press within three seconds closes.</summary>
    private bool ConfirmsClose(BrowserTab tab)
    {
        if (!tab.IsPinned || !tabSettings.ProtectPinnedTabs) return true;
        var now = time.GetUtcNow();
        if (pinnedCloseRequest is { } request && request.TabId == tab.Id && now - request.At < PinnedCloseWindow)
        {
            pinnedCloseRequest = null;
            return true;
        }
        pinnedCloseRequest = (tab.Id, now);
        Notice = $"“{tab.DisplayTitle}” is pinned. Press Ctrl+W again to close it.";
        return false;
    }

    /// <summary>Next/previous tab of the current workspace, skipping collapsed groups. Always handles it.</summary>
    private bool SelectAdjacentNavigable(int offset)
    {
        var order = NavigableTabs;
        if (selectedTab is not { } tab || order.Count < 2) return true;
        var index = order.IndexOf(tab);
        if (index < 0) return true;
        Select(order[((index + offset) % order.Count + order.Count) % order.Count]);
        return true;
    }

    // ---- Wiring ----

    /// <summary>Subscribes to the tab list once (the first hook runs inside the constructor).</summary>
    private void EnsureTabsWired()
    {
        if (tabsWired) return;
        tabsWired = true;
        Tabs.CollectionChanged += TabsCollectionChanged;
        foreach (var tab in Tabs) WatchTab(tab);
    }

    private void TabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (BrowserTab tab in e.OldItems)
            {
                if (!Tabs.Contains(tab)) UnwatchTab(tab);
            }
        }
        if (e.NewItems is not null)
        {
            foreach (BrowserTab tab in e.NewItems) WatchTab(tab);
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var tab in Tabs) WatchTab(tab);
        }
        SyncVisibleTabs();
    }

    private readonly Dictionary<BrowserTab, (PropertyChangedEventHandler Handler, IChromiumAudio? Audio, Action? AudioHandler)> watchedTabs = [];

    private void WatchTab(BrowserTab tab)
    {
        if (watchedTabs.ContainsKey(tab)) return;
        PropertyChangedEventHandler handler = (_, e) => WatchedTabChanged(tab, e.PropertyName);
        tab.PropertyChanged += handler;
        watchedTabs[tab] = (handler, null, null);
        AttachAudio(tab);
    }

    private void UnwatchTab(BrowserTab tab)
    {
        if (!watchedTabs.Remove(tab, out var watch)) return;
        tab.PropertyChanged -= watch.Handler;
        if (watch.Audio is not null && watch.AudioHandler is not null) watch.Audio.PlayingAudioChanged -= watch.AudioHandler;
    }

    private void WatchedTabChanged(BrowserTab tab, string? property)
    {
        switch (property)
        {
            case nameof(BrowserTab.GeckoId):
                // A new Gecko tab (opened, reopened after unloading, moved from Chromium) starts unmuted.
                if (tab is { IsMuted: true, GeckoId: { } id }) Gecko.Send("setMuted", id, true);
                break;
            case nameof(BrowserTab.Chromium):
                AttachAudio(tab);
                break;
            case nameof(BrowserTab.Url):
            case nameof(BrowserTab.Title):
                // Page changes are saved within a few seconds, not on every one.
                ScheduleSessionSave(PageSaveDelay);
                break;
        }
    }

    /// <summary>Follows the audio of the tab's current Chromium browser and applies its mute state.</summary>
    private void AttachAudio(BrowserTab tab)
    {
        if (!watchedTabs.TryGetValue(tab, out var watch)) return;
        if (watch.Audio is not null && watch.AudioHandler is not null) watch.Audio.PlayingAudioChanged -= watch.AudioHandler;
        var audio = tab.Chromium as IChromiumAudio;
        Action? audioHandler = null;
        if (audio is not null)
        {
            audioHandler = () =>
            {
                if (ReferenceEquals(tab.Chromium, audio)) tab.IsPlayingAudio = audio.IsPlayingAudio;
            };
            audio.PlayingAudioChanged += audioHandler;
            if (tab.IsMuted) audio.IsMuted = true;
            tab.IsPlayingAudio = audio.IsPlayingAudio;
        }
        else if (tab.Engine == EngineKind.Chromium)
        {
            tab.IsPlayingAudio = false;
        }
        watchedTabs[tab] = (watch.Handler, audio, audioHandler);
    }

    /// <summary>Brings <see cref="VisibleTabs"/> in line with the selected workspace's tabs, with as few moves as possible.</summary>
    private void SyncVisibleTabs()
    {
        var target = TabsIn(SelectedWorkspaceId);
        for (var i = 0; i < target.Count; i++)
        {
            if (i < VisibleTabs.Count && ReferenceEquals(VisibleTabs[i], target[i])) continue;
            var existing = IndexOfReference(VisibleTabs, target[i], i);
            if (existing >= 0) VisibleTabs.Move(existing, i);
            else VisibleTabs.Insert(i, target[i]);
        }
        while (VisibleTabs.Count > target.Count) VisibleTabs.RemoveAt(VisibleTabs.Count - 1);
    }

    private static int IndexOfReference(IList<BrowserTab> list, BrowserTab tab, int start)
    {
        for (var i = start; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], tab)) return i;
        }
        return -1;
    }

    /// <summary>After any change to order, pins, groups or workspaces.</summary>
    private void OrganizationChanged()
    {
        SyncVisibleTabs();
        TabOrganizationChanged?.Invoke();
        ScheduleSessionSave();
    }

    // ---- Ordering helpers ----

    internal List<TabSlot> TabSlots() =>
        Tabs.Select(t => new TabSlot(t.Id, WorkspaceIdOf(t), t.IsPinned, t.GroupId)).ToList();

    /// <summary>Applies an order computed by <see cref="TabOrdering"/> to the live tabs.</summary>
    private void ApplyOrder(IReadOnlyList<TabSlot> slots)
    {
        var byId = Tabs.ToDictionary(t => t.Id);
        var ordered = new List<BrowserTab>(slots.Count);
        foreach (var slot in slots)
        {
            if (!byId.TryGetValue(slot.Id, out var tab)) continue;
            tab.IsPinned = slot.IsPinned;
            tab.GroupId = slot.GroupId;
            tab.WorkspaceId = slot.WorkspaceId;
            ordered.Add(tab);
        }
        if (ordered.Count == Tabs.Count)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ReferenceEquals(Tabs[i], ordered[i])) continue;
                Tabs.Move(IndexOfReference(Tabs, ordered[i], i), i);
            }
        }
        PruneEmptyGroups();
        OrganizationChanged();
    }

    private void NormalizeTabOrder() => ApplyOrder(TabOrdering.Normalized(TabSlots()));

    private void PruneEmptyGroups()
    {
        var used = Tabs.Where(t => t.GroupId is not null).Select(t => t.GroupId!.Value).ToHashSet();
        for (var i = Groups.Count - 1; i >= 0; i--)
        {
            if (!used.Contains(Groups[i].Id)) Groups.RemoveAt(i);
        }
    }
}
