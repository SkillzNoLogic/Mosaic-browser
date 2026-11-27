import AppKit
import MosaicCore

// Tab behaviour: workspaces, groups, pinning, reordering, Recently Closed, duplicate
// detection and tab search. The ordering rules themselves are pure and unit-tested in
// MosaicCore/Tabs/TabOrdering.swift; this file applies them to the live tabs.
//
// Close semantics (documented in the README and the settings page):
// - `close(_:)` always closes; it is the deliberate action (a tab's close button, a menu
//   item naming the tab). `closeSelectedTab()` (⌘W, also from inside Gecko) closes a pinned
//   tab only when pressed twice within three seconds, unless that protection is off.
// - Deleting a workspace closes its tabs. Callers in the UI must confirm first when
//   `workspaceDeletionNeedsConfirmation(_:)` is true; the closed tabs form one Recently
//   Closed entry, so ⌘⇧T brings the whole workspace back.
// - Closing a group is one Recently Closed entry as well.
extension BrowserModel {
    // MARK: - Queries

    /// The workspace on screen.
    var selectedWorkspace: Workspace {
        workspaces.first { $0.id == selectedWorkspaceID } ?? workspaces[0]
    }

    /// The workspace a tab belongs to (every tab has one; the first workspace is the
    /// fallback for a tab that somehow lacks it).
    func workspaceID(of tab: BrowserTab) -> UUID {
        tab.workspaceID ?? workspaces.first?.id ?? selectedWorkspaceID
    }

    func workspace(of tab: BrowserTab) -> Workspace? {
        let id = workspaceID(of: tab)
        return workspaces.first { $0.id == id }
    }

    /// A workspace's tabs in tab-strip order: pinned first, groups contiguous.
    func tabs(in workspaceID: UUID) -> [BrowserTab] {
        tabs.filter { self.workspaceID(of: $0) == workspaceID }
    }

    /// The selected workspace's tabs, in order. This is what a tab strip or sidebar shows
    /// (`tabs` holds every workspace's).
    var visibleTabs: [BrowserTab] { tabs(in: selectedWorkspaceID) }
    var pinnedTabs: [BrowserTab] { visibleTabs.filter(\.isPinned) }
    var unpinnedTabs: [BrowserTab] { visibleTabs.filter { !$0.isPinned } }

    /// Visible tabs that keyboard navigation visits: members of collapsed groups are
    /// skipped (except the selected tab).
    var navigableTabs: [BrowserTab] {
        let collapsed = Set(groups.filter(\.isCollapsed).map(\.id))
        return visibleTabs.filter { tab in
            tab.id == selectedTabID || tab.groupID.map { !collapsed.contains($0) } ?? true
        }
    }

    func group(withID id: UUID?) -> TabGroup? {
        guard let id else { return nil }
        return groups.first { $0.id == id }
    }

    func group(of tab: BrowserTab) -> TabGroup? { group(withID: tab.groupID) }

    func tabs(inGroup id: UUID) -> [BrowserTab] { tabs.filter { $0.groupID == id } }

    func tab(withID id: UUID) -> BrowserTab? { tabs.first { $0.id == id } }

    // MARK: - Placement (hooks called from BrowserModel.swift)

    /// Inserts a new tab according to the ordering rules: after `anchor` (normally the
    /// selected tab), never among pinned tabs; `linked` (opened from a link in `anchor`)
    /// puts it right after its opener and in its group.
    func insertTab(_ tab: BrowserTab, after anchor: BrowserTab?, linked: Bool) {
        if tab.workspaceID == nil { tab.workspaceID = selectedWorkspaceID }
        let placement = TabOrdering.insertion(
            in: tabSlots, workspace: workspaceID(of: tab), after: anchor?.id, joiningAnchorGroup: linked)
        tab.groupID = placement.groupID
        tabs.insert(tab, at: placement.index)
        scheduleSessionSave()
    }

    /// A page asked for a new tab (target=_blank, window.open): open it next to the page.
    func openLinkedTab(url: String, from source: BrowserTab) {
        let tab = makeTab(engine: source.engine)
        tab.workspaceID = workspaceID(of: source)
        insertTab(tab, after: source, linked: true)
        if tab.workspaceID == selectedWorkspaceID { select(tab) }
        load(url, in: tab)
    }

    /// Bookkeeping whenever a tab becomes the selected one, by Mosaic or by Gecko.
    func tabSelected(_ tab: BrowserTab, previous: BrowserTab?) {
        let now = Date()
        if let previous, previous !== tab { previous.lastActivated = now }
        tab.lastActivated = now
        let workspace = workspaceID(of: tab)
        if tab.workspaceID == nil { tab.workspaceID = workspace }
        if selectedWorkspaceID != workspace { selectedWorkspaceID = workspace }
        tabsFeature.selectionByWorkspace[workspace] = tab.id
        if let id = tab.groupID, let index = groups.firstIndex(where: { $0.id == id }), groups[index].isCollapsed {
            groups[index].isCollapsed = false
        }
        if tabsFeature.pinnedCloseRequest?.tabID != tab.id { tabsFeature.pinnedCloseRequest = nil }
        wakeIfAsleep(tab)
        scheduleSessionSave()
    }

    /// The tab to select when the selected `tab` closes or leaves: its right-hand
    /// neighbour in the workspace, else the left one; nil when it was the last tab.
    func successorAfterClosing(_ tab: BrowserTab) -> BrowserTab? {
        guard tab.id == selectedTabID else { return nil }
        let order = navigableTabs
        if let index = order.firstIndex(of: tab) {
            if index + 1 < order.count { return order[index + 1] }
            if index > 0 { return order[index - 1] }
        }
        return visibleTabs.first { $0 !== tab }
    }

    /// Cleanup after `tab` left the list.
    func tabRemoved(_ tab: BrowserTab) {
        for (workspace, id) in tabsFeature.selectionByWorkspace where id == tab.id {
            tabsFeature.selectionByWorkspace[workspace] = nil
        }
        if let offer = tabsFeature.duplicateOffer, offer.tabID == tab.id || offer.existingTabID == tab.id {
            tabsFeature.duplicateOffer = nil
        }
        tabsFeature.downloadingTabs.remove(tab.id)
        pruneEmptyGroups()
        scheduleSessionSave()
    }

    /// ⌘W protection for pinned tabs: the first press only explains, a second press
    /// within three seconds closes.
    func confirmsClose(_ tab: BrowserTab) -> Bool {
        guard tab.isPinned, tabsFeature.settings.protectPinnedTabs else { return true }
        if let request = tabsFeature.pinnedCloseRequest, request.tabID == tab.id,
           Date().timeIntervalSince(request.at) < 3 {
            tabsFeature.pinnedCloseRequest = nil
            return true
        }
        tabsFeature.pinnedCloseRequest = (tab.id, Date())
        notice = "“\(tab.displayTitle)” is pinned. Press ⌘W again to close it."
        return false
    }

    // MARK: - Pin, mute, duplicate

    /// Pinned tabs sit at the front of their workspace and leave any group. Pinning puts
    /// the tab after the other pinned tabs; unpinning puts it first after them.
    func setPinned(_ pinned: Bool, for tab: BrowserTab) {
        guard tab.isPinned != pinned, tabs.contains(tab) else { return }
        let othersPinned = tabs(in: workspaceID(of: tab)).filter { $0.isPinned && $0 !== tab }.count
        tab.isPinned = pinned
        if pinned { tab.groupID = nil }
        applyOrder(TabOrdering.move(tabSlots, id: tab.id, to: othersPinned))
    }

    func togglePin(_ tab: BrowserTab) { setPinned(!tab.isPinned, for: tab) }

    /// Only the flag: the engine area applies it to the engines by observing `isMuted`.
    func setMuted(_ muted: Bool, for tab: BrowserTab) {
        guard tab.isMuted != muted else { return }
        tab.isMuted = muted
        if tab.id == selectedTabID { objectWillChange.send() }
        scheduleSessionSave()
    }

    func toggleMute(_ tab: BrowserTab) { setMuted(!tab.isMuted, for: tab) }

    /// Opens the same page in a new tab next to `tab` (same engine, workspace, group, pin
    /// and mute state). The page is loaded afresh: back/forward history is not copied.
    @discardableResult
    func duplicate(_ tab: BrowserTab, select: Bool = true) -> BrowserTab {
        let copy = makeTab(engine: tab.engine)
        copy.workspaceID = workspaceID(of: tab)
        copy.isMuted = tab.isMuted
        copy.title = tab.title
        if tab.isPinned, let index = tabs.firstIndex(of: tab) {
            copy.isPinned = true
            tabs.insert(copy, at: index + 1)
            scheduleSessionSave()
        } else {
            insertTab(copy, after: tab, linked: true)
        }
        if select { self.select(copy) }
        if !tab.isStartPage { load(tab.url, in: copy) }
        return copy
    }

    // MARK: - Reordering

    /// Drag and drop: moves `tab` to position `index` among its workspace's tabs (the
    /// position it has after the move, 0 = first). Clamped to its section (pinned tabs
    /// stay in front); dropped inside a group it joins it, dragged out it leaves it.
    func moveTab(_ tab: BrowserTab, to index: Int) {
        guard tabs.contains(tab) else { return }
        applyOrder(TabOrdering.move(tabSlots, id: tab.id, to: index))
    }

    /// Moves a whole group so that its first tab lands at `index` among the workspace's
    /// other tabs; never among pinned tabs or inside another group.
    func moveGroup(_ id: UUID, to index: Int) {
        applyOrder(TabOrdering.moveGroup(tabSlots, groupID: id, to: index))
    }

    /// ⌘1…⌘8 select that tab of the workspace, ⌘9 the last one.
    func selectTab(number: Int) {
        let order = navigableTabs
        guard !order.isEmpty, number >= 1 else { return }
        select(number >= 9 || number > order.count ? order[order.count - 1] : order[number - 1])
    }

    // MARK: - Groups

    /// Makes a group of `members` (unpinning them), gathered where the first one was.
    /// Members must share a workspace; others are ignored.
    @discardableResult
    func createGroup(with members: [BrowserTab], name: String = "", color: TabGroup.Color? = nil) -> TabGroup? {
        guard let first = members.first(where: { tabs.contains($0) }) else { return nil }
        let workspace = workspaceID(of: first)
        let used = Set(groups.map(\.color))
        let group = TabGroup(
            name: name.trimmingCharacters(in: .whitespacesAndNewlines),
            color: color ?? TabGroup.Color.allCases.first { !used.contains($0) } ?? .blue)
        groups.append(group)
        for tab in members where tabs.contains(tab) && workspaceID(of: tab) == workspace {
            tab.isPinned = false
            tab.groupID = group.id
        }
        normalizeTabOrder()
        return group
    }

    /// Adds `tab` at the end of a group (moving it to the group's workspace if needed).
    func addTab(_ tab: BrowserTab, toGroup id: UUID) {
        guard tabs.contains(tab), tab.groupID != id, let last = tabs(inGroup: id).last else { return }
        let workspace = workspaceID(of: last)
        if workspaceID(of: tab) != workspace { moveTab(tab, toWorkspace: workspace) }
        tab.isPinned = false
        tab.groupID = id
        let others = tabs(in: workspace).filter { $0 !== tab }
        let index = (others.lastIndex { $0.groupID == id } ?? others.count - 1) + 1
        applyOrder(TabOrdering.move(tabSlots, id: tab.id, to: index))
    }

    /// Takes `tab` out of its group, placing it just after the group.
    func removeTabFromGroup(_ tab: BrowserTab) {
        guard let id = tab.groupID else { return }
        let others = tabs(in: workspaceID(of: tab)).filter { $0 !== tab }
        let index = (others.lastIndex { $0.groupID == id }).map { $0 + 1 } ?? others.count
        tab.groupID = nil
        applyOrder(TabOrdering.move(tabSlots, id: tab.id, to: index))
    }

    func renameGroup(_ id: UUID, to name: String) {
        updateGroup(id) { $0.name = name.trimmingCharacters(in: .whitespacesAndNewlines) }
    }

    func setGroupColor(_ id: UUID, _ color: TabGroup.Color) {
        updateGroup(id) { $0.color = color }
    }

    /// Collapsing the group that holds the selected tab selects the nearest tab outside it
    /// (opening a new tab if there is none), as Chrome does.
    func setGroupCollapsed(_ id: UUID, _ collapsed: Bool) {
        guard let current = group(withID: id), current.isCollapsed != collapsed else { return }
        updateGroup(id) { $0.isCollapsed = collapsed }
        guard collapsed, let selected = selectedTab, selected.groupID == id else { return }
        if let outside = nearestTab(to: selected, where: { $0.groupID != id }) {
            select(outside)
        } else {
            newTab()
        }
    }

    func toggleGroupCollapsed(_ id: UUID) {
        if let group = group(withID: id) { setGroupCollapsed(id, !group.isCollapsed) }
    }

    /// Dissolves the group; its tabs stay where they are.
    func ungroup(_ id: UUID) {
        for tab in tabs(inGroup: id) { tab.groupID = nil }
        groups.removeAll { $0.id == id }
        scheduleSessionSave()
    }

    /// Closes every tab of the group; ⌘⇧T reopens the group as a whole.
    func closeGroup(_ id: UUID) {
        guard let group = group(withID: id) else { return }
        let members = tabs(inGroup: id)
        if let selected = selectedTab, selected.groupID == id {
            if let outside = nearestTab(to: selected, where: { $0.groupID != id }) { select(outside) } else { newTab() }
        }
        let entry = ClosedItem(kind: .group, tabs: [], group: SessionGroup(group))
        closingAsOneEntry(entry) {
            for tab in members { close(tab) }
        }
        groups.removeAll { $0.id == id }
    }

    // MARK: - Workspaces

    /// Adds a workspace and (by default) switches to it, which opens a new tab there.
    @discardableResult
    func createWorkspace(name: String? = nil, symbol: String = "square.grid.2x2", switchTo: Bool = true) -> Workspace {
        let trimmed = name?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let workspace = Workspace(name: trimmed.isEmpty ? nextWorkspaceName() : trimmed, symbol: symbol)
        workspaces.append(workspace)
        scheduleSessionSave()
        if switchTo { selectWorkspace(workspace.id) }
        return workspace
    }

    func renameWorkspace(_ id: UUID, to name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let index = workspaces.firstIndex(where: { $0.id == id }) else { return }
        workspaces[index].name = trimmed
        scheduleSessionSave()
    }

    func setWorkspaceSymbol(_ id: UUID, _ symbol: String) {
        guard let index = workspaces.firstIndex(where: { $0.id == id }) else { return }
        workspaces[index].symbol = symbol
        scheduleSessionSave()
    }

    /// Reorders the workspace list (the switcher's order).
    func moveWorkspace(_ id: UUID, to index: Int) {
        guard let from = workspaces.firstIndex(where: { $0.id == id }) else { return }
        let workspace = workspaces.remove(at: from)
        workspaces.insert(workspace, at: min(max(index, 0), workspaces.count))
        scheduleSessionSave()
    }

    /// True when deleting would close tabs that show something; the UI must confirm then.
    func workspaceDeletionNeedsConfirmation(_ id: UUID) -> Bool {
        tabs(in: id).contains { !$0.isStartPage }
    }

    /// Deletes a workspace and closes its tabs (one Recently Closed entry: ⌘⇧T restores
    /// the workspace with its tabs). The last workspace cannot be deleted. Confirmation is
    /// the caller's job; see `workspaceDeletionNeedsConfirmation`.
    func deleteWorkspace(_ id: UUID) {
        guard workspaces.count > 1, let index = workspaces.firstIndex(where: { $0.id == id }) else { return }
        let workspace = workspaces[index]
        if selectedWorkspaceID == id {
            selectWorkspace(workspaces[index == 0 ? 1 : index - 1].id)
        }
        let entry = ClosedItem(kind: .workspace, tabs: [], workspace: SessionWorkspace(workspace))
        closingAsOneEntry(entry) {
            for tab in tabs(in: id) { close(tab) }
        }
        workspaces.removeAll { $0.id == id }
        tabsFeature.selectionByWorkspace[id] = nil
        scheduleSessionSave()
    }

    /// Shows a workspace: its previously selected tab (or its first), or a new tab if it
    /// has none. Selecting the tab is what moves Gecko's docking along.
    func selectWorkspace(_ id: UUID) {
        guard id != selectedWorkspaceID, workspaces.contains(where: { $0.id == id }) else { return }
        let candidates = tabs(in: id)
        let remembered = tabsFeature.selectionByWorkspace[id].flatMap { rid in candidates.first { $0.id == rid } }
        let collapsed = Set(groups.filter(\.isCollapsed).map(\.id))
        let firstShown = candidates.first { $0.groupID.map { !collapsed.contains($0) } ?? true } ?? candidates.first
        if let target = remembered ?? firstShown {
            select(target)
        } else {
            selectedWorkspaceID = id
            newTab()
        }
        scheduleSessionSave()
    }

    func selectAdjacentWorkspace(offset: Int) {
        guard workspaces.count > 1, let index = workspaces.firstIndex(where: { $0.id == selectedWorkspaceID }) else { return }
        selectWorkspace(workspaces[(index + offset + workspaces.count) % workspaces.count].id)
    }

    /// Moves `tab` to the end of another workspace (leaving its group). You stay in the
    /// current workspace; if the tab was selected, its neighbour is selected instead.
    func moveTab(_ tab: BrowserTab, toWorkspace id: UUID) {
        guard tabs.contains(tab), workspaceID(of: tab) != id, workspaces.contains(where: { $0.id == id }) else { return }
        let wasSelected = tab.id == selectedTabID
        let successor = wasSelected ? successorAfterClosing(tab) : nil
        let oldWorkspace = workspaceID(of: tab)
        tab.groupID = nil
        var slots = tabSlots.filter { $0.id != tab.id }
        let index = TabOrdering.appendIndex(in: slots, workspace: id, pinned: tab.isPinned)
        slots.insert(TabSlot(id: tab.id, workspaceID: id, isPinned: tab.isPinned), at: index)
        tab.workspaceID = id
        if tabsFeature.selectionByWorkspace[oldWorkspace] == tab.id { tabsFeature.selectionByWorkspace[oldWorkspace] = nil }
        applyOrder(TabOrdering.normalized(slots))
        if wasSelected {
            if let successor { select(successor) } else { newTab() }
        }
    }

    // MARK: - Recently closed

    var recentlyClosedItems: [ClosedItem] { tabsFeature.recentlyClosed.items }

    /// ⌘⇧T: reopens the most recently closed tab, group or workspace.
    @discardableResult
    func reopenClosedTab() -> Bool {
        guard let item = tabsFeature.recentlyClosed.popLatest() else {
            notice = "No recently closed tabs."
            return false
        }
        restoreClosed(item)
        return true
    }

    func reopenClosed(_ id: UUID) {
        if let item = tabsFeature.recentlyClosed.remove(id: id) { restoreClosed(item) }
    }

    func clearRecentlyClosed() {
        tabsFeature.recentlyClosed.removeAll()
        scheduleSessionSave()
    }

    /// Adds `tab` to Recently Closed (or to the entry being collected).
    func recordClosed(_ tab: BrowserTab) {
        guard !tab.isStartPage else { return }
        let workspace = workspaceID(of: tab)
        let record = ClosedTabRecord(
            engine: tab.engine.rawValue, url: tab.url, title: tab.title, workspaceID: workspace,
            groupID: tab.groupID, isPinned: tab.isPinned, isMuted: tab.isMuted,
            index: tabs(in: workspace).firstIndex(of: tab) ?? 0)
        if tabsFeature.closingBatch != nil {
            tabsFeature.closingBatch?.tabs.append(record)
        } else {
            tabsFeature.recentlyClosed.push(ClosedItem(kind: .tab, tabs: [record]),
                                            limit: tabsFeature.settings.recentlyClosedLimit)
        }
    }

    /// Runs `body` (which closes tabs) so the closed tabs become one Recently Closed entry.
    func closingAsOneEntry(_ entry: ClosedItem, _ body: () -> Void) {
        tabsFeature.closingBatch = entry
        body()
        if let batch = tabsFeature.closingBatch {
            tabsFeature.recentlyClosed.push(batch, limit: tabsFeature.settings.recentlyClosedLimit)
        }
        tabsFeature.closingBatch = nil
    }

    /// Closes a tab that should not appear in Recently Closed.
    func closeWithoutRecording(_ tab: BrowserTab) {
        let collecting = tabsFeature.closingBatch
        tabsFeature.closingBatch = ClosedItem(kind: .tab, tabs: [])
        close(tab)
        tabsFeature.closingBatch = collecting
    }

    /// Brings an entry back where it was: its workspace (recreated if it was deleted with
    /// it), its group (recreated for a closed group), its position, engine, pin and mute
    /// state. The first tab is selected and loads; the others stay asleep until selected.
    private func restoreClosed(_ item: ClosedItem) {
        if let saved = item.workspace, !workspaces.contains(where: { $0.id == saved.id }) {
            workspaces.append(Workspace(id: saved.id, name: saved.name, symbol: saved.symbol))
        }
        if let saved = item.group, !groups.contains(where: { $0.id == saved.id }) {
            groups.append(TabGroup(saved))
        }
        var restored: [BrowserTab] = []
        // Reversed: each record's index was taken after the ones closed before it were gone.
        for record in item.tabs.reversed() {
            let tab = makeRestoredTab(engine: record.engine, url: record.url, title: record.title)
            let workspace = record.workspaceID.flatMap { id in workspaces.contains { $0.id == id } ? id : nil }
                ?? selectedWorkspaceID
            tab.workspaceID = workspace
            tab.isPinned = record.isPinned
            tab.isMuted = record.isMuted
            tab.groupID = record.groupID.flatMap { id in groups.contains { $0.id == id } ? id : nil }
            let siblings = tabs(in: workspace)
            let index: Int
            if record.index < siblings.count, let at = tabs.firstIndex(of: siblings[record.index]) {
                index = at
            } else if let last = siblings.last, let at = tabs.firstIndex(of: last) {
                index = at + 1
            } else {
                index = tabs.count
            }
            tabs.insert(tab, at: index)
            restored.insert(tab, at: 0)
        }
        normalizeTabOrder()
        if let first = restored.first { select(first) }
    }

    // MARK: - Duplicate tabs

    /// Another open tab (any workspace) showing the same page as `url`, if any.
    func existingTab(for url: String, excluding: BrowserTab? = nil) -> BrowserTab? {
        let candidates = tabs.map { (id: $0.id, url: $0.url) }
        return DuplicateFinder.firstMatch(for: url, among: candidates, excluding: excluding?.id).flatMap(tab(withID:))
    }

    /// After a navigation from the address bar: if the page is already open in another
    /// tab, offer (non-blocking) to switch to it.
    func offerSwitchIfAlreadyOpen(_ tab: BrowserTab, wasStartPage: Bool) {
        guard tabsFeature.settings.offerSwitchToOpenTab, let existing = existingTab(for: tab.url, excluding: tab) else { return }
        let workspace = workspace(of: existing)
        tabsFeature.duplicateOffer = TabsFeature.DuplicateOffer(
            tabID: tab.id, existingTabID: existing.id, title: existing.displayTitle,
            workspaceName: workspace?.id == selectedWorkspaceID ? nil : workspace?.name,
            closesNewTab: wasStartPage)
    }

    /// "Switch to Tab": selects the existing tab; a start-page tab that had just been used
    /// for the duplicate is closed (without entering Recently Closed).
    func acceptDuplicateOffer() {
        guard let offer = tabsFeature.duplicateOffer else { return }
        tabsFeature.duplicateOffer = nil
        guard let existing = tab(withID: offer.existingTabID) else { return }
        let duplicate = tab(withID: offer.tabID)
        select(existing)
        if offer.closesNewTab, let duplicate { closeWithoutRecording(duplicate) }
    }

    func dismissDuplicateOffer() {
        tabsFeature.duplicateOffer = nil
    }

    // MARK: - Tab search

    func showTabSearch() { tabsFeature.isTabSearchVisible = true }
    func hideTabSearch() { tabsFeature.isTabSearchVisible = false }
    func toggleTabSearch() { tabsFeature.isTabSearchVisible.toggle() }

    /// Open tabs of every workspace matching `query`, best first (current workspace first
    /// among equals).
    func tabSearchResults(_ query: String) -> [BrowserTab] {
        let current = workspaces.filter { $0.id == selectedWorkspaceID }
        let order = (current + workspaces.filter { $0.id != selectedWorkspaceID }).flatMap { tabs(in: $0.id) }
        return TabSearch.rank(order, query: query, title: \.displayTitle, url: \.url)
    }

    // MARK: - Events forwarded from Gecko (shortcuts pressed while a Gecko page has focus)

    func handleTabsEvent(_ type: String) {
        switch type {
        case "tabs.reopenClosed": reopenClosedTab()
        case "tabs.search":
            if allowsFocusChanges { NSApp.activate(ignoringOtherApps: true) }
            showTabSearch()
        case "tabs.next": selectAdjacentTab(offset: 1)
        case "tabs.previous": selectAdjacentTab(offset: -1)
        default:
            if type.hasPrefix("tabs.select"), let number = Int(type.dropFirst("tabs.select".count)) {
                selectTab(number: number)
            }
        }
    }

    // MARK: - Helpers

    var tabSlots: [TabSlot] {
        tabs.map { TabSlot(id: $0.id, workspaceID: workspaceID(of: $0), isPinned: $0.isPinned, groupID: $0.groupID) }
    }

    /// Applies an order computed by `TabOrdering` to the live tabs.
    func applyOrder(_ slots: [TabSlot]) {
        let byID = Dictionary(uniqueKeysWithValues: tabs.map { ($0.id, $0) })
        let ordered = slots.compactMap { slot -> BrowserTab? in
            guard let tab = byID[slot.id] else { return nil }
            if tab.isPinned != slot.isPinned { tab.isPinned = slot.isPinned }
            if tab.groupID != slot.groupID { tab.groupID = slot.groupID }
            if tab.workspaceID != slot.workspaceID { tab.workspaceID = slot.workspaceID }
            return tab
        }
        if ordered.count == tabs.count, ordered.map(\.id) != tabs.map(\.id) { tabs = ordered }
        pruneEmptyGroups()
        objectWillChange.send()
        scheduleSessionSave()
    }

    func normalizeTabOrder() {
        applyOrder(TabOrdering.normalized(tabSlots))
    }

    func pruneEmptyGroups() {
        let used = Set(tabs.compactMap(\.groupID))
        if groups.contains(where: { !used.contains($0.id) }) { groups.removeAll { !used.contains($0.id) } }
    }

    private func updateGroup(_ id: UUID, _ change: (inout TabGroup) -> Void) {
        guard let index = groups.firstIndex(where: { $0.id == id }) else { return }
        change(&groups[index])
        scheduleSessionSave()
    }

    /// The closest tab to `tab` in its workspace (right first) satisfying `condition`.
    private func nearestTab(to tab: BrowserTab, where condition: (BrowserTab) -> Bool) -> BrowserTab? {
        let order = tabs(in: workspaceID(of: tab))
        guard let index = order.firstIndex(of: tab) else { return nil }
        let right = order[(index + 1)...].first(where: condition)
        return right ?? order[..<index].last(where: condition)
    }

    private func nextWorkspaceName() -> String {
        var number = workspaces.count + 1
        while workspaces.contains(where: { $0.name == "Workspace \(number)" }) { number += 1 }
        return "Workspace \(number)"
    }
}

// MARK: - Conversions between the app's shapes and the saved ones

extension SessionWorkspace {
    init(_ workspace: Workspace) {
        self.init(id: workspace.id, name: workspace.name, symbol: workspace.symbol)
    }
}

extension SessionGroup {
    init(_ group: TabGroup) {
        self.init(id: group.id, name: group.name, color: group.color.rawValue, isCollapsed: group.isCollapsed)
    }
}

extension TabGroup {
    init(_ saved: SessionGroup) {
        self.init(id: saved.id, name: saved.name, color: Color(rawValue: saved.color) ?? .blue,
                  isCollapsed: saved.isCollapsed)
    }
}
