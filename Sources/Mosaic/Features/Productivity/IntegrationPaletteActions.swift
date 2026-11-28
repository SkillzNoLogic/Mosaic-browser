import MosaicCore
import SwiftUI

/// Command-palette actions for features built by other areas (tabs, privacy, shell), which
/// the productivity area could not reference while they were being built in parallel.
enum IntegrationPaletteActions {
    @MainActor
    static func actions(_ model: BrowserModel) -> [PaletteAction] {
        var items: [PaletteAction] = []
        let tab = model.selectedTab

        // Tabs area.
        if let tab, !tab.isStartPage {
            items.append(PaletteAction(
                id: "tabs.pin", title: tab.isPinned ? "Unpin Tab" : "Pin Tab", subtitle: tab.displayTitle,
                keywords: ["pin", "keep"], symbol: tab.isPinned ? "pin.slash" : "pin") { model.togglePin(tab) })
            items.append(PaletteAction(
                id: "tabs.mute", title: tab.isMuted ? "Unmute Tab" : "Mute Tab", subtitle: tab.displayTitle,
                keywords: ["sound", "audio", "silence"],
                symbol: tab.isMuted ? "speaker.wave.2" : "speaker.slash") { model.toggleMute(tab) })
            items.append(PaletteAction(
                id: "tabs.unload", title: "Unload Tab", subtitle: "Free its memory until you select it again",
                keywords: ["sleep", "discard", "memory"], symbol: "moon.zzz") { model.unloadTab(tab) })
            items.append(PaletteAction(
                id: "tabs.group", title: "Add Tab to New Group", keywords: ["group"],
                symbol: "rectangle.stack.badge.plus") { _ = model.createGroup(with: [tab]) })
        }
        if !model.recentlyClosedItems.isEmpty {
            items.append(PaletteAction(
                id: "tabs.reopen", title: "Reopen Closed Tab", keywords: ["undo", "restore", "closed"],
                symbol: "arrow.uturn.backward.square") { _ = model.reopenClosedTab() })
        }
        items.append(PaletteAction(
            id: "tabs.search", title: "Search Tabs", keywords: ["find tab", "switch"],
            symbol: "rectangle.and.text.magnifyingglass") { model.toggleTabSearch() })
        items.append(PaletteAction(
            id: "tabs.newWorkspace", title: "New Workspace", keywords: ["space", "workspace"],
            symbol: "square.grid.2x2.fill") { _ = model.createWorkspace() })
        for workspace in model.workspaces where workspace.id != model.selectedWorkspaceID {
            items.append(PaletteAction(
                id: "tabs.workspace.\(workspace.id.uuidString)", title: "Switch to \(workspace.name)",
                keywords: ["workspace", "space"], symbol: workspace.symbol) { model.selectWorkspace(workspace.id) })
        }

        // Privacy area.
        items.append(PaletteAction(
            id: "privacy.privateTab", title: "New Private Tab", keywords: ["incognito", "private"],
            symbol: "eyeglasses") { model.newPrivateTab() })
        items.append(PaletteAction(
            id: "privacy.clear", title: "Clear Browsing Data…", keywords: ["history", "cookies", "cache", "delete"],
            symbol: "trash") { model.showClearBrowsingData() })

        // Shell.
        for preset in MosaicPresetID.allCases where preset != model.mosaics.activePreset {
            items.append(PaletteAction(
                id: "shell.mosaic.\(preset.rawValue)", title: "Switch to \(preset.title) Mosaic",
                keywords: ["layout", "theme", "appearance", "mosaic"], symbol: "square.grid.2x2") {
                    model.applyMosaic(preset)
                })
        }
        items.append(PaletteAction(
            id: "shell.sidebar", title: model.isSidebarVisible ? "Hide Sidebar" : "Show Sidebar",
            keywords: ["sidebar", "tabs"], symbol: "sidebar.left") { model.toggleSidebar() })
        items.append(PaletteAction(
            id: "shell.home", title: "Go Home", keywords: ["homepage", "start"], symbol: "house") { model.goHome() })
        return items
    }
}
