using Mosaic.Core.Tabs;

namespace Mosaic.Core.Browser;

// Tabs area, part 2: pin, mute, duplicate, reordering, groups, workspaces, Recently Closed,
// duplicate detection and tab search. Port of
// Sources/Mosaic/Features/Tabs/BrowserModel+Organization.swift.
//
// Close semantics (as on macOS):
// - Close(tab) always closes; it is the deliberate action (a tab's close button, a menu item
//   naming the tab). CloseSelectedTab() (Ctrl+W, also from inside Gecko) closes a pinned tab
//   only when pressed twice within three seconds, unless that protection is off.
// - Deleting a workspace closes its tabs. The UI must confirm first when
//   WorkspaceDeletionNeedsConfirmation is true; the closed tabs form one Recently Closed entry,
//   so Ctrl+Shift+T brings the whole workspace back.
// - Closing a group is one Recently Closed entry as well.
public sealed partial class BrowserModel
{
    // ---- Pin, mute, duplicate ----

    /// <summary>
    /// Pinned tabs sit at the front of their workspace and leave any group. Pinning puts the tab
    /// after the other pinned tabs; unpinning puts it first after them.
    /// </summary>
    public void SetPinned(bool pinned, BrowserTab tab)
    {
        if (tab.IsPinned == pinned || !Tabs.Contains(tab)) return;
        var othersPinned = TabsIn(WorkspaceIdOf(tab)).Count(t => t.IsPinned && !ReferenceEquals(t, tab));
        tab.IsPinned = pinned;
        if (pinned) tab.GroupId = null;
        ApplyOrder(TabOrdering.Move(TabSlots(), tab.Id, othersPinned));
    }

    public void TogglePin(BrowserTab tab) => SetPinned(!tab.IsPinned, tab);

    /// <summary>Mutes or unmutes the tab's page in whichever engine shows it.</summary>
    public void SetMuted(bool muted, BrowserTab tab)
    {
        if (tab.IsMuted == muted) return;
        tab.IsMuted = muted;
        if (tab is { Engine: EngineKind.Gecko, GeckoId: { } id }) Gecko.Send("setMuted", id, muted);
        if (tab.Chromium is IChromiumAudio audio) audio.IsMuted = muted;
        ScheduleSessionSave();
    }

    public void ToggleMute(BrowserTab tab) => SetMuted(!tab.IsMuted, tab);

    /// <summary>
    /// Opens the same page in a new tab next to <paramref name="tab"/> (same engine, workspace,
    /// group, pin and mute state). The page is loaded afresh: back/forward history is not copied.
    /// </summary>
    public BrowserTab Duplicate(BrowserTab tab, bool select = true)
    {
        var copy = new BrowserTab(tab.Engine)
        {
            WorkspaceId = WorkspaceIdOf(tab),
            IsMuted = tab.IsMuted,
            Title = tab.Title,
            LastActivated = time.GetUtcNow(),
        };
        EnsureTabsWired();
        if (tab.IsPinned && Tabs.IndexOf(tab) is var index and >= 0)
        {
            copy.IsPinned = true;
            Tabs.Insert(index + 1, copy);
            OrganizationChanged();
        }
        else
        {
            linkSource = tab;
            try { InsertTab(copy); }
            finally { linkSource = null; }
        }
        if (select) Select(copy);
        if (!tab.IsStartPage) Load(tab.Url, copy);
        return copy;
    }

    // ---- Reordering (the shell's drag and drop) ----

    /// <summary>
    /// Drag and drop: moves <paramref name="tab"/> to position <paramref name="index"/> among its
    /// workspace's tabs (the position it has after the move, 0 = first). Clamped to its section
    /// (pinned tabs stay in front); dropped inside a group it joins it, dragged out it leaves it.
    /// </summary>
    public void MoveTab(BrowserTab tab, int index)
    {
        if (!Tabs.Contains(tab)) return;
        ApplyOrder(TabOrdering.Move(TabSlots(), tab.Id, index));
    }

    /// <summary>Drag and drop by neighbour: puts <paramref name="tab"/> just before <paramref name="before"/> (null: at the end).</summary>
    public void MoveTabBefore(BrowserTab tab, BrowserTab? before)
    {
        if (!Tabs.Contains(tab) || ReferenceEquals(tab, before)) return;
        var others = TabsIn(WorkspaceIdOf(tab)).Where(t => !ReferenceEquals(t, tab)).ToList();
        var index = before is null ? others.Count : others.IndexOf(before);
        MoveTab(tab, index < 0 ? others.Count : index);
    }

    /// <summary>
    /// Moves a whole group so that its first tab lands at <paramref name="index"/> among the
    /// workspace's other tabs; never among pinned tabs or inside another group.
    /// </summary>
    public void MoveGroup(Guid id, int index) => ApplyOrder(TabOrdering.MoveGroup(TabSlots(), id, index));

    /// <summary>Moves a whole group just before <paramref name="before"/> (null: to the end).</summary>
    public void MoveGroupBefore(Guid id, BrowserTab? before)
    {
        var members = TabsInGroup(id);
        if (members.Count == 0) return;
        var others = TabsIn(WorkspaceIdOf(members[0])).Where(t => t.GroupId != id).ToList();
        var index = before is null ? others.Count : others.IndexOf(before);
        MoveGroup(id, index < 0 ? others.Count : index);
    }

    /// <summary>Ctrl+1…Ctrl+8 select that tab of the workspace, Ctrl+9 the last one.</summary>
    public void SelectTabNumber(int number)
    {
        var order = NavigableTabs;
        if (order.Count == 0 || number < 1) return;
        Select(number >= 9 || number > order.Count ? order[^1] : order[number - 1]);
    }

    // ---- Groups ----

    /// <summary>
    /// Makes a group of <paramref name="members"/> (unpinning them), gathered where the first one
    /// was. Members must share a workspace; others are ignored.
    /// </summary>
    public TabGroup? CreateGroup(IEnumerable<BrowserTab> members, string name = "", TabGroupColor? color = null)
    {
        var list = members.Where(Tabs.Contains).ToList();
        if (list.Count == 0) return null;
        var workspace = WorkspaceIdOf(list[0]);
        var used = Groups.Select(g => g.Color).ToHashSet();
        var chosen = color ?? Enum.GetValues<TabGroupColor>().Cast<TabGroupColor?>().FirstOrDefault(c => !used.Contains(c!.Value)) ?? TabGroupColor.Blue;
        var group = new TabGroup(Guid.NewGuid(), name.Trim(), chosen);
        Groups.Add(group);
        foreach (var tab in list.Where(t => WorkspaceIdOf(t) == workspace))
        {
            tab.IsPinned = false;
            tab.GroupId = group.Id;
        }
        NormalizeTabOrder();
        return group;
    }

    /// <summary>Adds <paramref name="tab"/> at the end of a group (moving it to the group's workspace if needed).</summary>
    public void AddTabToGroup(BrowserTab tab, Guid id)
    {
        if (!Tabs.Contains(tab) || tab.GroupId == id || TabsInGroup(id).LastOrDefault() is not { } last) return;
        var workspace = WorkspaceIdOf(last);
        if (WorkspaceIdOf(tab) != workspace) MoveTabToWorkspace(tab, workspace);
        tab.IsPinned = false;
        tab.GroupId = id;
        var others = TabsIn(workspace).Where(t => !ReferenceEquals(t, tab)).ToList();
        var index = others.FindLastIndex(t => t.GroupId == id) + 1;
        ApplyOrder(TabOrdering.Move(TabSlots(), tab.Id, index <= 0 ? others.Count : index));
    }

    /// <summary>Takes <paramref name="tab"/> out of its group, placing it just after the group.</summary>
    public void RemoveTabFromGroup(BrowserTab tab)
    {
        if (tab.GroupId is not { } id) return;
        var others = TabsIn(WorkspaceIdOf(tab)).Where(t => !ReferenceEquals(t, tab)).ToList();
        var last = others.FindLastIndex(t => t.GroupId == id);
        tab.GroupId = null;
        ApplyOrder(TabOrdering.Move(TabSlots(), tab.Id, last >= 0 ? last + 1 : others.Count));
    }

    public void RenameGroup(Guid id, string name) => UpdateGroup(id, g => g.Name = name.Trim());

    public void SetGroupColor(Guid id, TabGroupColor color) => UpdateGroup(id, g => g.Color = color);

    /// <summary>
    /// Collapsing the group that holds the selected tab selects the nearest tab outside it
    /// (opening a new tab if there is none), as Chrome does.
    /// </summary>
    public void SetGroupCollapsed(Guid id, bool collapsed)
    {
        if (GroupWithId(id) is not { } group || group.IsCollapsed == collapsed) return;
        UpdateGroup(id, g => g.IsCollapsed = collapsed);
        if (!collapsed || selectedTab is not { } selected || selected.GroupId != id) return;
        if (NearestTab(selected, t => t.GroupId != id) is { } outside) Select(outside);
        else NewTab();
    }

    public void ToggleGroupCollapsed(Guid id)
    {
        if (GroupWithId(id) is { } group) SetGroupCollapsed(id, !group.IsCollapsed);
    }

    /// <summary>Dissolves the group; its tabs stay where they are.</summary>
    public void Ungroup(Guid id)
    {
        foreach (var tab in TabsInGroup(id)) tab.GroupId = null;
        if (GroupWithId(id) is { } group) Groups.Remove(group);
        OrganizationChanged();
    }

    /// <summary>Closes every tab of the group; Ctrl+Shift+T reopens the group as a whole.</summary>
    public void CloseGroup(Guid id)
    {
        if (GroupWithId(id) is not { } group) return;
        var members = TabsInGroup(id);
        if (selectedTab is { } selected && selected.GroupId == id)
        {
            if (NearestTab(selected, t => t.GroupId != id) is { } outside) Select(outside);
            else NewTab();
        }
        ClosingAsOneEntry(new ClosedItem { Kind = ClosedItemKind.Group, Group = group.ToSession(), ClosedAt = time.GetUtcNow() }, () =>
        {
            foreach (var tab in members) Close(tab);
        });
        Groups.Remove(group);
        OrganizationChanged();
    }

    // ---- Workspaces ----

    /// <summary>Adds a workspace and (by default) switches to it, which opens a new tab there.</summary>
    public Workspace CreateWorkspace(string? name = null, string symbol = SessionWorkspace.DefaultSymbol, bool switchTo = true)
    {
        var trimmed = name?.Trim() ?? "";
        var workspace = new Workspace(Guid.NewGuid(), trimmed.Length == 0 ? NextWorkspaceName() : trimmed, symbol);
        Workspaces.Add(workspace);
        OrganizationChanged();
        if (switchTo) SelectWorkspace(workspace.Id);
        return workspace;
    }

    public void RenameWorkspace(Guid id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || Workspaces.FirstOrDefault(w => w.Id == id) is not { } workspace) return;
        workspace.Name = trimmed;
        OrganizationChanged();
    }

    public void SetWorkspaceSymbol(Guid id, string symbol)
    {
        if (Workspaces.FirstOrDefault(w => w.Id == id) is not { } workspace) return;
        workspace.Symbol = symbol;
        OrganizationChanged();
    }

    /// <summary>Reorders the workspace list (the switcher's order).</summary>
    public void MoveWorkspace(Guid id, int index)
    {
        var from = IndexOfWorkspace(id);
        if (from < 0) return;
        Workspaces.Move(from, Math.Clamp(index, 0, Workspaces.Count - 1));
        OrganizationChanged();
    }

    /// <summary>True when deleting would close tabs that show something; the UI must confirm then.</summary>
    public bool WorkspaceDeletionNeedsConfirmation(Guid id) => TabsIn(id).Any(t => !t.IsStartPage);

    /// <summary>
    /// Deletes a workspace and closes its tabs (one Recently Closed entry: Ctrl+Shift+T restores
    /// the workspace with its tabs). The last workspace cannot be deleted. Confirmation is the
    /// caller's job; see <see cref="WorkspaceDeletionNeedsConfirmation"/>.
    /// </summary>
    public void DeleteWorkspace(Guid id)
    {
        var index = IndexOfWorkspace(id);
        if (Workspaces.Count < 2 || index < 0) return;
        var workspace = Workspaces[index];
        if (SelectedWorkspaceId == id) SelectWorkspace(Workspaces[index == 0 ? 1 : index - 1].Id);
        ClosingAsOneEntry(new ClosedItem { Kind = ClosedItemKind.Workspace, Workspace = workspace.ToSession(), ClosedAt = time.GetUtcNow() }, () =>
        {
            foreach (var tab in TabsIn(id)) Close(tab);
        });
        Workspaces.Remove(workspace);
        selectionByWorkspace.Remove(id);
        OrganizationChanged();
    }

    /// <summary>
    /// Shows a workspace: its previously selected tab (or its first), or a new tab if it has none.
    /// Selecting the tab is what moves Gecko's docking along.
    /// </summary>
    public void SelectWorkspace(Guid id)
    {
        if (id == SelectedWorkspaceId || IndexOfWorkspace(id) < 0) return;
        var candidates = TabsIn(id);
        var remembered = selectionByWorkspace.TryGetValue(id, out var rid) ? candidates.FirstOrDefault(t => t.Id == rid) : null;
        var collapsed = Groups.Where(g => g.IsCollapsed).Select(g => g.Id).ToHashSet();
        var firstShown = candidates.FirstOrDefault(t => t.GroupId is not { } g || !collapsed.Contains(g)) ?? candidates.FirstOrDefault();
        if ((remembered ?? firstShown) is { } target)
        {
            Select(target);
        }
        else
        {
            SelectedWorkspaceId = id;
            NewTab();
        }
        ScheduleSessionSave();
    }

    public void SelectAdjacentWorkspace(int offset)
    {
        var index = IndexOfWorkspace(SelectedWorkspaceId);
        if (Workspaces.Count < 2 || index < 0) return;
        SelectWorkspace(Workspaces[((index + offset) % Workspaces.Count + Workspaces.Count) % Workspaces.Count].Id);
    }

    /// <summary>
    /// Moves <paramref name="tab"/> to the end of another workspace (leaving its group). You stay
    /// in the current workspace; if the tab was selected, its neighbour is selected instead.
    /// </summary>
    public void MoveTabToWorkspace(BrowserTab tab, Guid id)
    {
        if (!Tabs.Contains(tab) || WorkspaceIdOf(tab) == id || IndexOfWorkspace(id) < 0) return;
        var wasSelected = ReferenceEquals(tab, selectedTab);
        var successor = wasSelected ? SuccessorAfterClosing(tab) : null;
        var oldWorkspace = WorkspaceIdOf(tab);
        tab.GroupId = null;
        var slots = TabSlots().Where(s => s.Id != tab.Id).ToList();
        var index = TabOrdering.AppendIndex(slots, id, tab.IsPinned);
        slots.Insert(index, new TabSlot(tab.Id, id, tab.IsPinned));
        tab.WorkspaceId = id;
        if (selectionByWorkspace.TryGetValue(oldWorkspace, out var remembered) && remembered == tab.Id) selectionByWorkspace.Remove(oldWorkspace);
        ApplyOrder(TabOrdering.Normalized(slots));
        if (!wasSelected) return;
        if (successor is not null) Select(successor);
        else
        {
            SelectedWorkspaceId = oldWorkspace;
            NewTab();
        }
    }

    // ---- Recently closed ----

    private RecentlyClosedList recentlyClosed = new();

    /// <summary>Newest first. Persisted with the session.</summary>
    public IReadOnlyList<ClosedItem> RecentlyClosedItems => recentlyClosed.Items;

    /// <summary>Raised when <see cref="RecentlyClosedItems"/> changes.</summary>
    public event Action? RecentlyClosedChanged;

    /// <summary>Ctrl+Shift+T: reopens the most recently closed tab, group or workspace.</summary>
    public bool ReopenClosedTab()
    {
        if (recentlyClosed.PopLatest() is not { } item)
        {
            Notice = "No recently closed tabs.";
            return false;
        }
        RecentlyClosedDidChange();
        RestoreClosed(item);
        return true;
    }

    public void ReopenClosed(Guid id)
    {
        if (recentlyClosed.Remove(id) is not { } item) return;
        RecentlyClosedDidChange();
        RestoreClosed(item);
    }

    public void ClearRecentlyClosed()
    {
        recentlyClosed.Clear();
        RecentlyClosedDidChange();
    }

    /// <summary>Adds <paramref name="tab"/> to Recently Closed (or to the entry being collected).</summary>
    private void RecordClosed(BrowserTab tab)
    {
        // Private tabs leave no trace, not even in Recently Closed.
        if (tab.IsStartPage || IsPrivateTab(tab)) return;
        var workspace = WorkspaceIdOf(tab);
        var record = new ClosedTabRecord(
            tab.Engine.Id(), tab.Url, tab.Title, workspace, tab.GroupId, tab.IsPinned, tab.IsMuted,
            Math.Max(0, TabsIn(workspace).IndexOf(tab)));
        if (closingBatch is not null)
        {
            closingRecords.Add(record);
            return;
        }
        recentlyClosed.Push(new ClosedItem { Kind = ClosedItemKind.Tab, Tabs = [record], ClosedAt = time.GetUtcNow() }, tabSettings.RecentlyClosedLimit);
        RecentlyClosedDidChange();
    }

    /// <summary>Runs <paramref name="body"/> (which closes tabs) so the closed tabs become one Recently Closed entry.</summary>
    private void ClosingAsOneEntry(ClosedItem entry, Action body)
    {
        var outerBatch = closingBatch;
        var outerRecords = closingRecords.ToList();
        closingBatch = entry;
        closingRecords.Clear();
        try
        {
            body();
            if (entry.Kind != ClosedItemKind.Tab || closingRecords.Count > 0)
            {
                recentlyClosed.Push(entry with { Tabs = closingRecords.ToList() }, tabSettings.RecentlyClosedLimit);
            }
        }
        finally
        {
            closingBatch = outerBatch;
            closingRecords.Clear();
            closingRecords.AddRange(outerRecords);
        }
        RecentlyClosedDidChange();
    }

    /// <summary>Closes a tab that should not appear in Recently Closed.</summary>
    private void CloseWithoutRecording(BrowserTab tab)
    {
        var outerBatch = closingBatch;
        var outerRecords = closingRecords.ToList();
        closingBatch = new ClosedItem { Kind = ClosedItemKind.Tab };
        try
        {
            Close(tab);
        }
        finally
        {
            closingBatch = outerBatch;
            closingRecords.Clear();
            closingRecords.AddRange(outerRecords);
        }
    }

    /// <summary>
    /// Brings an entry back where it was: its workspace (recreated if it was deleted with it), its
    /// group (recreated for a closed group), its position, engine, pin and mute state. The first
    /// tab is selected and loads; the others stay asleep until selected.
    /// </summary>
    private void RestoreClosed(ClosedItem item)
    {
        if (item.Workspace is { } savedWorkspace && IndexOfWorkspace(savedWorkspace.Id) < 0)
        {
            Workspaces.Add(Workspace.FromSession(savedWorkspace));
        }
        if (item.Group is { } savedGroup && GroupWithId(savedGroup.Id) is null) Groups.Add(TabGroup.FromSession(savedGroup));
        var restored = new List<BrowserTab>();
        // Reversed: each record's index was taken after the ones closed before it were gone.
        foreach (var record in item.Tabs.Reverse())
        {
            var tab = MakeRestoredTab(record.Engine, record.Url, record.Title);
            var workspace = record.WorkspaceId is { } wid && IndexOfWorkspace(wid) >= 0 ? wid : SelectedWorkspaceId;
            tab.WorkspaceId = workspace;
            tab.IsPinned = record.IsPinned;
            tab.IsMuted = record.IsMuted;
            tab.GroupId = record.GroupId is { } gid && GroupWithId(gid) is not null ? gid : null;
            var siblings = TabsIn(workspace);
            int index;
            if (record.Index < siblings.Count) index = Tabs.IndexOf(siblings[record.Index]);
            else if (siblings.Count > 0) index = Tabs.IndexOf(siblings[^1]) + 1;
            else index = Tabs.Count;
            Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
            restored.Insert(0, tab);
        }
        NormalizeTabOrder();
        if (restored.Count > 0) Select(restored[0]);
    }

    /// <summary>
    /// A tab recreated from saved state: asleep (its page loads when it is selected), on the
    /// saved engine if this PC has it. Not yet in <see cref="Tabs"/>.
    /// </summary>
    private BrowserTab MakeRestoredTab(string engineName, string url, string title)
    {
        EnsureTabsWired();
        var kind = EngineKindExtensions.FromId(engineName) ?? EngineKind.Gecko;
        if (kind == EngineKind.Chromium && !chromium.IsAvailable) kind = EngineKind.Gecko;
        var tab = new BrowserTab(kind)
        {
            Url = url,
            Title = title,
            LastActivated = time.GetUtcNow(),
        };
        tab.IsAsleep = url.Length > 0 && !tab.IsInternalPage;
        return tab;
    }

    private void RecentlyClosedDidChange()
    {
        RecentlyClosedChanged?.Invoke();
        ScheduleSessionSave();
    }

    // ---- Duplicate tabs ----

    /// <summary>Another open tab (any workspace) showing the same page as <paramref name="url"/>, if any.</summary>
    public BrowserTab? ExistingTab(string url, BrowserTab? excluding = null) =>
        DuplicateFinder.FirstMatch(url, Tabs.Select(t => (t.Id, t.Url)), excluding?.Id) is { } id ? TabWithId(id) : null;

    /// <summary>
    /// Before a navigation from the address bar: if the page is already open in another tab,
    /// offer (non-blocking, for ten seconds) to switch to it.
    /// </summary>
    private void OfferSwitchIfAlreadyOpen(BrowserTab tab, string url)
    {
        if (!tabSettings.OfferSwitchToOpenTab || ExistingTab(url, tab) is not { } existing) return;
        var workspace = WorkspaceOf(existing);
        var offer = new DuplicateOffer(
            Guid.NewGuid(), tab.Id, existing.Id, existing.DisplayTitle,
            workspace is not null && workspace.Id != SelectedWorkspaceId ? workspace.Name : null,
            ClosesNewTab: tab.IsStartPage);
        DuplicateOffer = offer;
        _ = ExpireDuplicateOfferAsync(offer);
    }

    private async Task ExpireDuplicateOfferAsync(DuplicateOffer offer)
    {
        await Task.Delay(DuplicateOfferDuration, time);
        if (ReferenceEquals(duplicateOffer, offer)) DuplicateOffer = null;
    }

    /// <summary>
    /// "Switch to Tab": selects the existing tab; a start-page tab that had just been used for the
    /// duplicate is closed (without entering Recently Closed).
    /// </summary>
    public void AcceptDuplicateOffer()
    {
        if (duplicateOffer is not { } offer) return;
        DuplicateOffer = null;
        if (TabWithId(offer.ExistingTabId) is not { } existing) return;
        var duplicate = TabWithId(offer.TabId);
        Select(existing);
        if (offer.ClosesNewTab && duplicate is not null) CloseWithoutRecording(duplicate);
    }

    public void DismissDuplicateOffer() => DuplicateOffer = null;

    // ---- Tab search ----

    public void ShowTabSearch() => IsTabSearchVisible = true;
    public void HideTabSearch() => IsTabSearchVisible = false;
    public void ToggleTabSearch() => IsTabSearchVisible = !IsTabSearchVisible;

    /// <summary>Open tabs of every workspace matching <paramref name="query"/>, best first (current workspace first among equals).</summary>
    public List<BrowserTab> TabSearchResults(string query)
    {
        var current = SelectedWorkspaceId;
        var order = TabsIn(current).Concat(Workspaces.Where(w => w.Id != current).SelectMany(w => TabsIn(w.Id)));
        return TabSearch.Rank(order, query, t => t.DisplayTitle, t => t.Url);
    }

    /// <summary>Recently closed entries matching <paramref name="query"/> (five without a query, ten with one).</summary>
    public List<ClosedItem> ClosedSearchResults(string query) =>
        recentlyClosed.Items
            .Where(item => TabSearch.Score(query, item.Title, item.Tabs.FirstOrDefault()?.Url ?? "") is not null)
            .Take(query.Trim().Length == 0 ? 5 : 10)
            .ToList();

    // ---- Events forwarded from Gecko (shortcuts pressed while a Gecko page has focus) ----

    /// <summary>Raised when tab search should take keyboard focus from Gecko (Ctrl+Shift+A pressed in a Gecko page).</summary>
    public event Action? TabSearchFocusRequested;

    /// <summary>"tabs.reopenClosed", "tabs.search", "tabs.next", "tabs.previous", "tabs.select1"…"tabs.select9".</summary>
    public void HandleTabsEvent(string type)
    {
        switch (type)
        {
            case "tabs.reopenClosed":
                ReopenClosedTab();
                break;
            case "tabs.search":
                RequestWindowActivation();
                ShowTabSearch();
                if (AllowsFocusChanges) TabSearchFocusRequested?.Invoke();
                break;
            case "tabs.next":
                SelectAdjacentTab(1);
                break;
            case "tabs.previous":
                SelectAdjacentTab(-1);
                break;
            default:
                if (type.StartsWith("tabs.select", StringComparison.Ordinal)
                    && int.TryParse(type.AsSpan("tabs.select".Length), out var number))
                {
                    SelectTabNumber(number);
                }
                break;
        }
    }

    // ---- Helpers ----

    private int IndexOfWorkspace(Guid id)
    {
        for (var i = 0; i < Workspaces.Count; i++)
        {
            if (Workspaces[i].Id == id) return i;
        }
        return -1;
    }

    private void UpdateGroup(Guid id, Action<TabGroup> change)
    {
        if (GroupWithId(id) is not { } group) return;
        change(group);
        OrganizationChanged();
    }

    /// <summary>The closest tab to <paramref name="tab"/> in its workspace (right first) satisfying <paramref name="condition"/>.</summary>
    private BrowserTab? NearestTab(BrowserTab tab, Func<BrowserTab, bool> condition)
    {
        var order = TabsIn(WorkspaceIdOf(tab));
        var index = order.IndexOf(tab);
        if (index < 0) return null;
        return order.Skip(index + 1).FirstOrDefault(condition) ?? order.Take(index).LastOrDefault(condition);
    }

    private string NextWorkspaceName()
    {
        var number = Workspaces.Count + 1;
        while (Workspaces.Any(w => w.Name == $"Workspace {number}")) number++;
        return $"Workspace {number}";
    }
}
