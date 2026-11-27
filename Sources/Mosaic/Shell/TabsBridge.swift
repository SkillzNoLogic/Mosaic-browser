import SwiftUI

// The shell's only contact with tab *behaviour* (owned by the tabs area). Every call the
// shell makes about workspaces, pinning, groups, sleeping tabs and reordering goes through
// here, under shell-prefixed names, so the shell's views stay independent of how the tabs
// area implements them.

enum TabsBridge {
    static let tabsBehaviourAvailable = true
}

extension BrowserModel {
    /// Tabs of the selected workspace in strip order.
    var shellVisibleTabs: [BrowserTab] { visibleTabs }

    func shellSelectWorkspace(_ id: UUID) {
        selectWorkspace(id)
    }

    func shellCreateWorkspace() {
        _ = createWorkspace()
    }

    func shellWorkspaceDeletionNeedsConfirmation(_ id: UUID) -> Bool {
        workspaceDeletionNeedsConfirmation(id)
    }

    func shellDeleteWorkspace(_ id: UUID) {
        deleteWorkspace(id)
    }

    /// Drag and drop: `index` is the tab's position among the workspace's tabs after the move.
    func shellMoveTab(_ tab: BrowserTab, to index: Int) {
        moveTab(tab, to: index)
    }

    func shellMoveGroup(_ id: UUID, to index: Int) {
        moveGroup(id, to: index)
    }

    func shellToggleGroupCollapsed(_ id: UUID) {
        toggleGroupCollapsed(id)
    }

    func shellTogglePin(_ tab: BrowserTab) {
        togglePin(tab)
    }

    func shellToggleMute(_ tab: BrowserTab) {
        toggleMute(tab)
    }

    func shellDuplicate(_ tab: BrowserTab) {
        _ = duplicate(tab)
    }
}

extension BrowserTab {
    var shellIsAsleep: Bool { isAsleep }

    /// Opacity for a sleeping tab's icon and title.
    static var shellAsleepOpacity: Double { BrowserTab.asleepOpacity }
}

extension TabGroup.Color {
    var shellSwatch: Color { swatch }
}

/// The tabs area's overlays (tab search ⌘⇧A, "already open" notice, crash recovery), placed
/// once over the whole window.
struct ShellTabsOverlayMount: View {
    var body: some View { TabsOverlay() }
}
