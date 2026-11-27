import Combine
import Foundation
import MosaicCore

// Session save and restore, and crash recovery.
//
// The session (tabs with engine, URL, title, pinned, muted, group and workspace; groups;
// workspaces; each workspace's selected tab; Recently Closed) is written to
// <profile>/Session/session.json about a second after anything changes, every 15 seconds
// if a page changed, and on quit. A marker file tells a crash from a quit.
//
// At launch, per Settings › Tabs & Workspaces:
// - "Open previous session": everything comes back. After a crash too (with a notice).
// - "Start page": one new tab; workspaces and Recently Closed are always kept, pinned
//   tabs too if "Keep pinned tabs" is on. The rest is kept as the previous session
//   (History › Reopen All Tabs from Last Session). After a crash, a banner offers it.
// Restored tabs are asleep: only the selected one loads; the others load when selected.
extension BrowserModel {
    /// Called once from `init`, in place of opening the first tab.
    func startTabs() {
        let feature = tabsFeature
        guard !feature.isStarted else { return }
        feature.previousRun = feature.store.beginRun()

        var snapshot: SessionSnapshot?
        do {
            snapshot = try feature.store.load()
        } catch {
            let kept = feature.store.setAsideUnreadable()
            notice = "The saved session could not be read"
                + (kept.map { "; it was kept as \($0.lastPathComponent)" } ?? "") + "."
        }

        if let snapshot {
            restoreStructure(of: snapshot)
            let crashed = feature.previousRun == .crashed
            switch feature.settings.startup {
            case .previousSession:
                restoreTabs(from: snapshot, selecting: true)
                feature.store.removePrevious()
                if crashed, snapshot.pageCount > 0 {
                    notice = "Mosaic did not quit properly last time. Your tabs were restored."
                }
            case .startPage:
                if feature.settings.keepPinnedTabs {
                    restoreTabs(from: snapshot, selecting: false) { $0.isPinned }
                }
                let leftOver = snapshot.tabs.contains { !$0.url.isEmpty && !feature.restoredSessionTabIDs.contains($0.id) }
                if leftOver {
                    feature.previousSession = snapshot
                    try? feature.store.savePrevious(snapshot)
                    feature.offersCrashRecovery = crashed
                } else {
                    feature.previousSession = (try? feature.store.loadPrevious()) ?? nil
                }
                // The start page itself, in front of any pinned tabs (which stay asleep).
                newTab()
            }
        }
        if visibleTabs.isEmpty {
            newTab()
        } else if selectedTab == nil {
            let remembered = tabsFeature.selectionByWorkspace[selectedWorkspaceID].flatMap(tab(withID:))
            if let first = remembered ?? navigableTabs.first { select(first) }
        }

        feature.isStarted = true
        feature.onSettingsChange = { [weak self] old in self?.tabSettingsChanged(from: old) }
        startSessionSaving()
        startSleepingTabs()
        saveSession()
    }

    /// Called from `shutdown`: the final save, then the clean-exit marker.
    func finishTabs() {
        let feature = tabsFeature
        guard feature.isStarted, !feature.isFinished else { return }
        feature.saveTask?.cancel()
        feature.periodicTask?.cancel()
        feature.sleepTask?.cancel()
        feature.memoryPressureSource?.cancel()
        saveSession()
        feature.isFinished = true
        feature.store.endRun()
    }

    // MARK: - Saving

    /// Saves about a second from now (changes in quick succession are written once).
    func scheduleSessionSave() {
        let feature = tabsFeature
        guard feature.isStarted, !feature.isFinished else { return }
        feature.saveTask?.cancel()
        feature.saveTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard !Task.isCancelled else { return }
            self?.saveSession()
        }
    }

    /// Writes the session now if it changed since the last write.
    func saveSession() {
        let feature = tabsFeature
        guard feature.isStarted, !feature.isFinished else { return }
        var snapshot = makeSessionSnapshot()
        snapshot.savedAt = .distantPast
        guard snapshot != feature.lastSavedSession else { return }
        let comparable = snapshot
        snapshot.savedAt = Date()
        do {
            try feature.store.save(snapshot)
            feature.lastSavedSession = comparable
            feature.saveFailureReported = false
        } catch {
            if !feature.saveFailureReported {
                feature.saveFailureReported = true
                notice = "Mosaic could not save your tabs: \(error.localizedDescription)"
            }
        }
    }

    func makeSessionSnapshot() -> SessionSnapshot {
        let selected = Set(tabsFeature.selectionByWorkspace.values).union(selectedTabID.map { [$0] } ?? [])
        return SessionSnapshot(
            workspaces: workspaces.map(SessionWorkspace.init),
            selectedWorkspaceID: selectedWorkspaceID,
            groups: groups.map(SessionGroup.init),
            tabs: tabs.map { tab in
                SessionTab(
                    id: tab.id, engine: tab.engine.rawValue, url: tab.url, title: tab.title,
                    isPinned: tab.isPinned, isMuted: tab.isMuted, workspaceID: workspaceID(of: tab),
                    groupID: tab.groupID, isSelected: selected.contains(tab.id))
            },
            recentlyClosed: tabsFeature.recentlyClosed.items)
    }

    private func startSessionSaving() {
        let feature = tabsFeature
        let changes: [AnyPublisher<Void, Never>] = [
            $tabs.map { _ in () }.eraseToAnyPublisher(),
            $selectedTabID.map { _ in () }.eraseToAnyPublisher(),
            $workspaces.map { _ in () }.eraseToAnyPublisher(),
            $selectedWorkspaceID.map { _ in () }.eraseToAnyPublisher(),
            $groups.map { _ in () }.eraseToAnyPublisher(),
            feature.$recentlyClosed.map { _ in () }.eraseToAnyPublisher(),
        ]
        Publishers.MergeMany(changes)
            .dropFirst(changes.count)
            .sink { [weak self] in self?.scheduleSessionSave() }
            .store(in: &feature.cancellables)
        // Page URLs and titles change without touching the lists above.
        feature.periodicTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 15_000_000_000)
                self?.saveSession()
            }
        }
    }

    private func tabSettingsChanged(from old: TabSettings) {
        let settings = tabsFeature.settings
        if settings.recentlyClosedLimit < old.recentlyClosedLimit {
            tabsFeature.recentlyClosed.trim(to: settings.recentlyClosedLimit)
        }
        if settings.sleep != old.sleep {
            Task { await self.runSleepCheck() }
        }
    }

    // MARK: - Restoring

    /// "Reopen All Tabs from Last Session" and the crash-recovery banner: brings back the
    /// session this launch did not restore (skipping tabs already restored, such as pinned
    /// tabs). The selected start-page tab, if any, is replaced.
    func restorePreviousSession() {
        guard let snapshot = tabsFeature.previousSession else { return }
        let lone = selectedTab?.isStartPage == true ? selectedTab : nil
        for saved in snapshot.workspaces where !workspaces.contains(where: { $0.id == saved.id }) {
            workspaces.append(Workspace(id: saved.id, name: saved.name, symbol: saved.symbol))
        }
        // Pinned tabs kept since that session (other tab IDs) must not be doubled.
        let openPinned = tabs.filter(\.isPinned).map(\.url)
        restoreTabs(from: snapshot, selecting: true) { saved in
            !(saved.isPinned && openPinned.contains { URLIdentity.samePage($0, saved.url) })
        }
        if let lone, tabs.count > 1, lone.id != selectedTabID { closeWithoutRecording(lone) }
        tabsFeature.previousSession = nil
        tabsFeature.offersCrashRecovery = false
        tabsFeature.store.removePrevious()
        scheduleSessionSave()
    }

    /// Pages `restorePreviousSession` would bring back (not counting tabs already restored).
    var previousSessionPageCount: Int {
        guard let snapshot = tabsFeature.previousSession else { return 0 }
        return snapshot.tabs.filter { !$0.url.isEmpty && !tabsFeature.restoredSessionTabIDs.contains($0.id) }.count
    }

    func dismissCrashRecovery() {
        tabsFeature.offersCrashRecovery = false
    }

    /// Workspaces and Recently Closed, which are kept whatever the startup setting.
    private func restoreStructure(of snapshot: SessionSnapshot) {
        workspaces = snapshot.workspaces.map { Workspace(id: $0.id, name: $0.name, symbol: $0.symbol) }
        selectedWorkspaceID = snapshot.selectedWorkspaceID ?? workspaces[0].id
        tabsFeature.recentlyClosed = RecentlyClosedList(snapshot.recentlyClosed)
        tabsFeature.recentlyClosed.trim(to: tabsFeature.settings.recentlyClosedLimit)
    }

    /// Recreates the snapshot's tabs (those passing `include`) at the end of the list,
    /// asleep, with their groups. With `selecting`, shows the snapshot's workspace and its
    /// selected tab, which is the one tab that loads.
    private func restoreTabs(
        from snapshot: SessionSnapshot, selecting: Bool, include: (SessionTab) -> Bool = { _ in true }
    ) {
        let feature = tabsFeature
        var restored: [BrowserTab] = []
        var selection: [UUID: BrowserTab] = [:]
        for saved in snapshot.tabs where include(saved) && !feature.restoredSessionTabIDs.contains(saved.id) {
            let tab = makeRestoredTab(engine: saved.engine, url: saved.url, title: saved.title)
            let workspace = saved.workspaceID.flatMap { id in workspaces.contains { $0.id == id } ? id : nil }
                ?? selectedWorkspaceID
            tab.workspaceID = workspace
            tab.isPinned = saved.isPinned
            tab.isMuted = saved.isMuted
            if let id = saved.groupID, let group = snapshot.groups.first(where: { $0.id == id }) {
                if !groups.contains(where: { $0.id == id }) { groups.append(TabGroup(group)) }
                tab.groupID = id
            }
            restored.append(tab)
            feature.restoredSessionTabIDs.insert(saved.id)
            if saved.isSelected { selection[workspace] = tab }
        }
        guard !restored.isEmpty else { return }
        tabs.append(contentsOf: restored)
        normalizeTabOrder()
        for (workspace, tab) in selection where feature.selectionByWorkspace[workspace] == nil || selecting {
            feature.selectionByWorkspace[workspace] = tab.id
        }
        guard selecting else { return }
        if let id = snapshot.selectedWorkspaceID, workspaces.contains(where: { $0.id == id }) {
            selectedWorkspaceID = id
        }
        if let target = selection[selectedWorkspaceID] ?? navigableTabs.first(where: { restored.contains($0) }) {
            select(target)
        }
    }

    /// A tab recreated from saved state: asleep (its page loads when it is selected),
    /// on the saved engine if this build has it.
    func makeRestoredTab(engine name: String, url: String, title: String) -> BrowserTab {
        var engine = EngineKind(rawValue: name) ?? .gecko
        if engine == .chromium, !chromiumAvailable { engine = .gecko }
        let tab = makeTab(engine: engine)
        tab.url = url
        tab.title = title
        tab.isAsleep = !url.isEmpty && InternalPage(url: url) == nil
        return tab
    }
}
