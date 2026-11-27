import Foundation
import MosaicCore

// Sleeping tabs: unloading background tabs to free memory, in both engines.
//
// - Chromium: the tab's browser is closed (`closeBrowser`); URL and title are kept and
//   the page is loaded again when the tab is selected. Back/forward history is lost.
// - Gecko: the agent discards the tab's browser (`Agent.discardTab`, which uses
//   `gBrowser.discardBrowser`); Gecko keeps the tab, its history and scroll position, and
//   restores it when Mosaic selects it. If the agent cannot discard, the Gecko tab is
//   closed instead and reopened from its URL when selected.
//
// The policy (MosaicCore/Tabs/SleepPolicy.swift) runs every 30 seconds and whenever macOS
// reports memory pressure. Exempt: the selected tab, tabs playing audio (Gecko only:
// Chromium does not report audio to Mosaic), tabs the downloads area marks as downloading,
// pinned tabs if so configured, and (automatic unloading only) Gecko pages that ask to
// stay loaded (a beforeunload handler, an open dialog).
extension BrowserModel {
    func startSleepingTabs() {
        let feature = tabsFeature
        feature.sleepTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 30_000_000_000)
                guard let self else { return }
                _ = await self.runSleepCheck()
            }
        }
        let source = DispatchSource.makeMemoryPressureSource(eventMask: [.normal, .warning, .critical], queue: .main)
        source.setEventHandler { [weak self, weak source] in
            guard let event = source?.data else { return }
            let level: MemoryPressure = event.contains(.critical) ? .critical : event.contains(.warning) ? .warning : .normal
            MainActor.assumeIsolated { self?.memoryPressureChanged(to: level) }
        }
        source.resume()
        feature.memoryPressureSource = source
    }

    /// Also used by automation to simulate pressure.
    func memoryPressureChanged(to level: MemoryPressure) {
        tabsFeature.memoryPressure = level
        guard level != .normal, tabsFeature.settings.sleep.respondToMemoryPressure else { return }
        Task { await self.runSleepCheck(pressure: level) }
    }

    /// Selecting a sleeping tab brings it back.
    func wakeIfAsleep(_ tab: BrowserTab) {
        guard tab.isAsleep else { return }
        tab.isAsleep = false
        // Keeps the blank page Gecko shows while restoring out of the address bar.
        tab.lastLoadRequest = Date()
        if tab.engine == .gecko, tab.geckoID != nil, gecko.isRunning {
            // A discarded Gecko tab: Gecko restores it when `select` sends selectTab.
            return
        }
        if !tab.isStartPage { load(tab.url, in: tab) }
    }

    /// Unloads `tab` ("Unload Tab"). The selected tab is unloaded after selecting its
    /// neighbour. `force` (manual unloading) discards Gecko pages even if they would
    /// rather stay loaded. Returns whether the tab is now asleep.
    @discardableResult
    func unload(_ tab: BrowserTab, force: Bool = true) async -> Bool {
        guard tabs.contains(tab), !tab.isAsleep, !tab.isStartPage, tab.internalPage == nil else { return false }
        if tab.id == selectedTabID {
            guard let successor = successorAfterClosing(tab) else { return false }
            select(successor)
        }
        switch tab.engine {
        case .chromium:
            if let view = tab.chromiumView {
                view.closeBrowser()
                tab.chromiumView = nil
            }
            tab.canGoBack = false
            tab.canGoForward = false
        case .gecko:
            if let id = tab.geckoID, gecko.isRunning {
                do {
                    guard (try await gecko.call("discardTab", id, force) as? Bool) == true else { return false }
                } catch {
                    // An agent without discardTab, or a failure: close the Gecko tab; it is
                    // reopened from its URL when selected.
                    guard tabs.contains(tab), tab.id != selectedTabID else { return false }
                    detachFromEngine(tab)
                }
            } else {
                tab.geckoWork?.cancel()
                tab.geckoWork = nil
            }
        }
        // Closed, or selected again, while Gecko was discarding it.
        guard tabs.contains(tab), tab.id != selectedTabID else { return false }
        tab.isAsleep = true
        tab.isLoading = false
        tab.isPlayingAudio = false
        objectWillChange.send()
        return true
    }

    /// Menu and palette entry point for `unload`.
    func unloadTab(_ tab: BrowserTab) {
        Task {
            if await !unload(tab) {
                notice = tab.isAsleep ? "This tab is already unloaded."
                    : visibleTabs.count < 2 && tab.id == selectedTabID ? "The only tab of a workspace can’t be unloaded."
                    : "This page can’t be unloaded right now."
            }
        }
    }

    /// Unloads every background tab that is not exempt, however recently it was used.
    func unloadBackgroundTabs() {
        Task {
            var settings = tabsFeature.settings.sleep
            settings.respondToMemoryPressure = true
            let unloaded = await runSleepCheck(pressure: .critical, settings: settings, force: true)
            notice = unloaded.isEmpty ? "No tabs to unload." : "Unloaded \(unloaded.count) \(unloaded.count == 1 ? "tab" : "tabs")."
        }
    }

    /// The downloads area reports downloads started from a tab; such tabs are not unloaded.
    func setDownloadActive(_ active: Bool, forTabID id: UUID) {
        if active { tabsFeature.downloadingTabs.insert(id) } else { tabsFeature.downloadingTabs.remove(id) }
    }

    var asleepTabCount: Int { tabs.filter(\.isAsleep).count }

    /// Applies the policy once. Returns the tabs it unloaded.
    @discardableResult
    func runSleepCheck(
        pressure: MemoryPressure? = nil, settings: SleepSettings? = nil, force: Bool = false
    ) async -> [UUID] {
        let feature = tabsFeature
        guard feature.isStarted, !feature.isFinished, !feature.isSleepCheckRunning else { return [] }
        feature.isSleepCheckRunning = true
        defer { feature.isSleepCheckRunning = false }
        await refreshGeckoActivity()
        let now = Date()
        let candidates = tabs.map(sleepCandidate)
        let ids = SleepPolicy.tabsToUnload(
            candidates, settings: settings ?? feature.settings.sleep,
            pressure: pressure ?? feature.memoryPressure, now: now)
        var unloaded: [UUID] = []
        for id in ids {
            if let tab = tab(withID: id), await unload(tab, force: force) { unloaded.append(id) }
        }
        feature.lastSleepCheck = now
        return unloaded
    }

    func sleepCandidate(_ tab: BrowserTab) -> SleepCandidate {
        SleepCandidate(
            id: tab.id, isSelected: tab.id == selectedTabID, isAsleep: tab.isAsleep,
            hasLoadedContent: !tab.isStartPage && tab.internalPage == nil && (tab.chromiumView != nil || tab.geckoID != nil),
            isPinned: tab.isPinned, isPlayingAudio: tab.isPlayingAudio,
            hasActiveDownload: tabsFeature.downloadingTabs.contains(tab.id), lastActive: tab.lastActivated)
    }

    /// Reads which Gecko tabs play audio, and which Gecko unloaded or restored by itself
    /// (Firefox has its own low-memory tab unloader), so `isAsleep` stays true to Gecko.
    func refreshGeckoActivity() async {
        guard gecko.isRunning, tabs.contains(where: { $0.engine == .gecko && $0.geckoID != nil }),
              let list = (try? await gecko.call("tabActivity")) as? [[String: Any]]
        else { return }
        var byID: [Int: [String: Any]] = [:]
        for entry in list { if let id = entry["id"] as? Int { byID[id] = entry } }
        for tab in tabs where tab.engine == .gecko {
            guard let id = tab.geckoID, let info = byID[id] else { continue }
            let audible = info["audible"] as? Bool ?? false
            if tab.isPlayingAudio != audible { tab.isPlayingAudio = audible }
            guard tab.id != selectedTabID, let discarded = info["discarded"] as? Bool else { continue }
            if discarded != tab.isAsleep { tab.isAsleep = discarded }
        }
    }
}
