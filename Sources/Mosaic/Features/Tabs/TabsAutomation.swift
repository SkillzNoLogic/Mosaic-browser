import AppKit
import MosaicCore
import SwiftUI

/// Automation commands and `dump` fields of the tabs area (see `Automation`).
///
/// A command that acts on a tab takes its index among the current workspace's tabs as
/// the value, or acts on the selected tab when the value is empty. Two-part values are
/// separated by "|". Workspaces and groups are named by name (or workspace index).
///
///     pin [i]  unpin [i]  togglePin [i]  mute [i]  unmute [i]  duplicate [i]
///     forceCloseTab [i]  selectVisible i  moveTab "from|to"  ageTab "i|minutes"
///     reopenClosed [n]  clearRecentlyClosed
///     newWorkspace [name]  selectWorkspace ws  renameWorkspace "ws|name"
///     deleteWorkspace ws  moveToWorkspace "ws" or "i|ws"  nextWorkspace  previousWorkspace
///     group [name|color]  addToGroup "group" or "i|group"  removeFromGroup [i]
///     ungroup g  closeGroup g  collapseGroup g  expandGroup g  renameGroup "g|name"
///     groupColor "g|color"  moveGroup "g|index"
///     unload [i]  unloadBackground  sleepCheck [normal|warning|critical]
///     memoryPressure normal|warning|critical  tabSearch show|hide|toggle  tabSearchQuery q
///     acceptDuplicate  dismissDuplicate  restoreSession  dismissRestore  saveSession
///     tabSetting "key=value"  tabEvent "tabs.search"  renderTabsView name (out: png path)
@MainActor
enum TabsAutomation {
    private static func with<T>(_ value: T?, _ body: (T) -> Void) {
        if let value { body(value) }
    }

    static let commands: Set<String> = [
        "pin", "unpin", "togglePin", "mute", "unmute", "duplicate", "forceCloseTab", "selectVisible",
        "moveTab", "ageTab", "reopenClosed", "clearRecentlyClosed", "newWorkspace", "selectWorkspace",
        "renameWorkspace", "deleteWorkspace", "moveToWorkspace", "nextWorkspace", "previousWorkspace",
        "group", "addToGroup", "removeFromGroup", "ungroup", "closeGroup", "collapseGroup", "expandGroup",
        "renameGroup", "groupColor", "moveGroup", "unload", "unloadBackground", "sleepCheck",
        "memoryPressure", "tabSearch", "tabSearchQuery", "acceptDuplicate", "dismissDuplicate", "restoreSession",
        "dismissRestore", "saveSession", "tabSetting", "tabEvent", "renderTabsView",
    ]

    static func handle(_ action: String, value: String, command: [String: Any], model: BrowserModel) {
        let parts = value.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
        func tab(_ text: String?) -> BrowserTab? {
            guard let text, !text.isEmpty else { return model.selectedTab }
            guard let index = Int(text), model.visibleTabs.indices.contains(index) else { return nil }
            return model.visibleTabs[index]
        }
        func workspace(_ text: String) -> Workspace? {
            if let index = Int(text), model.workspaces.indices.contains(index) { return model.workspaces[index] }
            return model.workspaces.first { $0.name == text }
        }
        func group(_ text: String) -> TabGroup? {
            model.groups.first { $0.name == text } ?? model.groups.first { $0.id.uuidString == text }
        }
        /// "i|x" → (tab i, x); "x" → (selected tab, x).
        func tabAndArgument() -> (BrowserTab?, String) {
            parts.count >= 2 ? (tab(parts[0]), parts[1]) : (model.selectedTab, value)
        }

        switch action {
        case "pin": with(tab(value)) { model.setPinned(true, for: $0) }
        case "unpin": with(tab(value)) { model.setPinned(false, for: $0) }
        case "togglePin": with(tab(value), model.togglePin)
        case "mute": with(tab(value)) { model.setMuted(true, for: $0) }
        case "unmute": with(tab(value)) { model.setMuted(false, for: $0) }
        case "duplicate": with(tab(value)) { model.duplicate($0) }
        case "forceCloseTab": with(tab(value), model.close)
        case "selectVisible": if let target = tab(value) { model.select(target) }
        case "moveTab":
            if parts.count == 2, let target = tab(parts[0]), let index = Int(parts[1]) { model.moveTab(target, to: index) }
        case "ageTab":
            if parts.count == 2, let target = tab(parts[0]), let minutes = Double(parts[1]) {
                target.lastActivated = Date().addingTimeInterval(-minutes * 60)
            }
        case "reopenClosed":
            if let index = Int(value), model.recentlyClosedItems.indices.contains(index) {
                model.reopenClosed(model.recentlyClosedItems[index].id)
            } else {
                model.reopenClosedTab()
            }
        case "clearRecentlyClosed": model.clearRecentlyClosed()
        case "newWorkspace": model.createWorkspace(name: value.isEmpty ? nil : value)
        case "selectWorkspace": with(workspace(value)) { model.selectWorkspace($0.id) }
        case "renameWorkspace":
            if parts.count == 2, let target = workspace(parts[0]) { model.renameWorkspace(target.id, to: parts[1]) }
        case "deleteWorkspace": with(workspace(value)) { model.deleteWorkspace($0.id) }
        case "moveToWorkspace":
            let (target, name) = tabAndArgument()
            if let target, let destination = workspace(name) { model.moveTab(target, toWorkspace: destination.id) }
        case "nextWorkspace": model.selectAdjacentWorkspace(offset: 1)
        case "previousWorkspace": model.selectAdjacentWorkspace(offset: -1)
        case "group":
            let color = parts.count > 1 ? TabGroup.Color(rawValue: parts[1]) : nil
            if let target = model.selectedTab { model.createGroup(with: [target], name: parts.first ?? "", color: color) }
        case "addToGroup":
            let (target, name) = tabAndArgument()
            if let target, let destination = group(name) { model.addTab(target, toGroup: destination.id) }
        case "removeFromGroup": with(tab(value), model.removeTabFromGroup)
        case "ungroup": with(group(value)) { model.ungroup($0.id) }
        case "closeGroup": with(group(value)) { model.closeGroup($0.id) }
        case "collapseGroup": with(group(value)) { model.setGroupCollapsed($0.id, true) }
        case "expandGroup": with(group(value)) { model.setGroupCollapsed($0.id, false) }
        case "renameGroup":
            if parts.count == 2, let target = group(parts[0]) { model.renameGroup(target.id, to: parts[1]) }
        case "groupColor":
            if parts.count == 2, let target = group(parts[0]), let color = TabGroup.Color(rawValue: parts[1]) {
                model.setGroupColor(target.id, color)
            }
        case "moveGroup":
            if parts.count == 2, let target = group(parts[0]), let index = Int(parts[1]) { model.moveGroup(target.id, to: index) }
        case "unload": if let target = tab(value) { Task { await model.unload(target) } }
        case "unloadBackground": model.unloadBackgroundTabs()
        case "sleepCheck": Task { await model.runSleepCheck(pressure: MemoryPressure(rawValue: value)) }
        case "memoryPressure": with(MemoryPressure(rawValue: value), model.memoryPressureChanged)
        case "tabSearch":
            switch value {
            case "hide": model.hideTabSearch()
            case "toggle": model.toggleTabSearch()
            default: model.showTabSearch()
            }
        case "tabSearchQuery": searchQuery = value
        case "acceptDuplicate": model.acceptDuplicateOffer()
        case "dismissDuplicate": model.dismissDuplicateOffer()
        case "restoreSession": model.restorePreviousSession()
        case "dismissRestore": model.dismissCrashRecovery()
        case "saveSession": model.saveSession()
        case "tabSetting": applySetting(value, to: model.tabsFeature)
        case "tabEvent": model.handleTabsEvent(value)
        case "renderTabsView": render(value, to: command["out"] as? String, model: model)
        default: break
        }
    }

    private static func applySetting(_ assignment: String, to feature: TabsFeature) {
        let pair = assignment.split(separator: "=", maxSplits: 1).map(String.init)
        guard pair.count == 2 else { return }
        let (key, value) = (pair[0], pair[1])
        let flag = value == "true"
        switch key {
        case "startup": with(TabSettings.Startup(rawValue: value)) { feature.settings.startup = $0 }
        case "keepPinnedTabs": feature.settings.keepPinnedTabs = flag
        case "protectPinnedTabs": feature.settings.protectPinnedTabs = flag
        case "offerSwitchToOpenTab": feature.settings.offerSwitchToOpenTab = flag
        case "recentlyClosedLimit": with(Int(value)) { feature.settings.recentlyClosedLimit = $0 }
        case "unloadInactiveTabs": feature.settings.sleep.unloadInactiveTabs = flag
        case "inactiveMinutes": with(Int(value)) { feature.settings.sleep.inactiveMinutes = $0 }
        case "neverUnloadPinned": feature.settings.sleep.neverUnloadPinned = flag
        case "respondToMemoryPressure": feature.settings.sleep.respondToMemoryPressure = flag
        default: break
        }
    }

    /// Draws one of the tabs area's views offscreen into a PNG, so it can be checked
    /// without the shell having placed it yet. Never shows a window.
    private static func render(_ name: String, to path: String?, model: BrowserModel) {
        guard let path else { return }
        let view: AnyView
        var size = CGSize(width: 720, height: 760)
        switch name {
        case "tabSearch":
            view = AnyView(TabSearchPanel(model: model).padding(30))
            size = CGSize(width: 640, height: 560)
        case "overlay":
            view = AnyView(TabsOverlay().background(Color(nsColor: .textBackgroundColor)))
            size = CGSize(width: 1000, height: 700)
        case "tabsSettings": view = AnyView(TabsSettingsPane())
        case "performanceSettings": view = AnyView(PerformanceSettingsPane())
        default: return
        }
        let host = NSHostingView(rootView: view.environmentObject(model))
        host.frame = CGRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        host.layoutSubtreeIfNeeded()
        // Let SwiftUI finish a layout pass before drawing.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
            host.layoutSubtreeIfNeeded()
            guard let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
            host.cacheDisplay(in: host.bounds, to: bitmap)
            try? bitmap.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
            window.contentView = nil
            window.close()
        }
    }

    // MARK: - dump

    /// Fields added to each tab in `dump`.
    static func tabFields(_ tab: BrowserTab, model: BrowserModel) -> [String: Any] {
        [
            "id": tab.id.uuidString,
            "pinned": tab.isPinned,
            "muted": tab.isMuted,
            "workspace": model.workspace(of: tab)?.name ?? "",
            "group": model.group(of: tab)?.name ?? (tab.groupID == nil ? "" : "?"),
            "asleep": tab.isAsleep,
            "playingAudio": tab.isPlayingAudio,
            "inactiveSeconds": Int(Date().timeIntervalSince(tab.lastActivated)),
        ]
    }

    /// Top-level `dump` fields, under "tabsArea".
    static func stateFields(model: BrowserModel) -> [String: Any] {
        let feature = model.tabsFeature
        let indices = Dictionary(uniqueKeysWithValues: model.tabs.enumerated().map { ($1.id, $0) })
        var state: [String: Any] = [
            "selectedWorkspace": model.selectedWorkspace.name,
            "workspaces": model.workspaces.map { workspace in
                [
                    "id": workspace.id.uuidString, "name": workspace.name, "symbol": workspace.symbol,
                    "selected": workspace.id == model.selectedWorkspaceID,
                    "tabs": model.tabs(in: workspace.id).count,
                ] as [String: Any]
            },
            "visibleTabs": model.visibleTabs.compactMap { indices[$0.id] },
            "visibleTitles": model.visibleTabs.map(\.displayTitle),
            "groups": model.groups.map { group in
                [
                    "id": group.id.uuidString, "name": group.name, "color": group.color.rawValue,
                    "collapsed": group.isCollapsed, "tabs": model.tabs(inGroup: group.id).map(\.displayTitle),
                ] as [String: Any]
            },
            "recentlyClosed": feature.recentlyClosed.items.map { item in
                [
                    "title": item.title, "kind": item.kind.rawValue, "urls": item.tabs.map(\.url),
                    "engines": item.tabs.map(\.engine),
                ] as [String: Any]
            },
            "offersCrashRecovery": feature.offersCrashRecovery,
            "previousSessionPages": feature.previousSession == nil ? -1 : model.previousSessionPageCount,
            "previousRun": "\(feature.previousRun)",
            "tabSearchVisible": feature.isTabSearchVisible,
            "tabSearchQuery": searchQuery,
            "tabSearchResults": model.tabSearchResults(searchQuery).map(\.displayTitle),
            "memoryPressure": feature.memoryPressure.rawValue,
            "asleep": model.asleepTabCount,
            "sessionFile": feature.store.sessionURL.path,
            "settings": (try? JSONSerialization.jsonObject(with: feature.settings.encoded())) ?? [:],
        ]
        if let offer = feature.duplicateOffer {
            state["duplicateOffer"] = [
                "title": offer.title, "closesNewTab": offer.closesNewTab,
                "existingIndex": indices[offer.existingTabID] ?? -1, "workspace": offer.workspaceName ?? "",
            ] as [String: Any]
        }
        return ["tabsArea": state]
    }

    /// Query for `tabSearchResults` in dumps (set with `tabSearchQuery`), to check filtering.
    private static var searchQuery = ""
}
