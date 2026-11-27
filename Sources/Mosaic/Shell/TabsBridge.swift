import SwiftUI

// The shell's only contact with tab *behaviour* (owned by the tabs area). Every call the
// shell makes about workspaces, pinning, groups, sleeping tabs and reordering goes through
// here, under shell-prefixed names so nothing collides with the tabs area's members.
//
// AFTER MERGING mac/tabs (integrator): set `tabsBehaviourAvailable = true` and replace each
// body with the call in its `merged:` comment. Nothing else in the shell needs to change.
// Until then the shell falls back to the contract members and hides actions that would do
// nothing (no dead buttons).

enum TabsBridge {
    /// merged: true
    static let tabsBehaviourAvailable = false
}

extension BrowserModel {
    /// Tabs of the selected workspace in strip order.  merged: `visibleTabs`
    var shellVisibleTabs: [BrowserTab] {
        tabs.filter { $0.workspaceID == nil || $0.workspaceID == selectedWorkspaceID }
    }

    /// merged: `selectWorkspace(id)`
    func shellSelectWorkspace(_ id: UUID) {
        selectedWorkspaceID = id
    }

    /// merged: `_ = createWorkspace()`
    func shellCreateWorkspace() {}

    /// merged: `workspaceDeletionNeedsConfirmation(id)`
    func shellWorkspaceDeletionNeedsConfirmation(_ id: UUID) -> Bool { true }

    /// merged: `deleteWorkspace(id)`
    func shellDeleteWorkspace(_ id: UUID) {}

    /// Drag and drop: `index` is the tab's position among the workspace's tabs after the
    /// move.  merged: `moveTab(tab, to: index)`
    func shellMoveTab(_ tab: BrowserTab, to index: Int) {}

    /// merged: `moveGroup(id, to: index)`
    func shellMoveGroup(_ id: UUID, to index: Int) {}

    /// merged: `toggleGroupCollapsed(id)`
    func shellToggleGroupCollapsed(_ id: UUID) {}

    /// merged: `togglePin(tab)`
    func shellTogglePin(_ tab: BrowserTab) {}

    /// merged: `toggleMute(tab)`
    func shellToggleMute(_ tab: BrowserTab) {}

    /// merged: `duplicate(tab)`
    func shellDuplicate(_ tab: BrowserTab) {
        newTab(url: tab.url, engine: tab.engine)
    }
}

extension BrowserTab {
    /// merged: `isAsleep`
    var shellIsAsleep: Bool { false }

    /// Opacity for a sleeping tab's icon and title.  merged: `BrowserTab.asleepOpacity`
    static var shellAsleepOpacity: Double { 0.55 }
}

extension TabGroup.Color {
    /// merged: `swatch`
    var shellSwatch: Color {
        switch self {
        case .grey: Color(nsColor: .systemGray)
        case .blue: Color(nsColor: .systemBlue)
        case .red: Color(nsColor: .systemRed)
        case .yellow: Color(nsColor: .systemYellow)
        case .green: Color(nsColor: .systemGreen)
        case .pink: Color(nsColor: .systemPink)
        case .purple: Color(nsColor: .systemPurple)
        case .cyan: Color(nsColor: .systemTeal)
        case .orange: Color(nsColor: .systemOrange)
        }
    }
}

/// The tabs area's overlays (tab search ⌘⇧A, "already open" notice, crash recovery), placed
/// once over the whole window.  merged: body = `TabsOverlay()`
struct ShellTabsOverlayMount: View {
    var body: some View { EmptyView() }
}
