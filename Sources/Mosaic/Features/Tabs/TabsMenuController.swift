import AppKit
import MosaicCore

/// The tabs area's menu items: a Tab menu, and Reopen Closed Tab / Recently Closed /
/// Reopen All Tabs from Last Session in the History menu. AppDelegate creates one, keeps
/// it alive, and adds what `makeTabMenu()` and `addHistoryItems(to:)` return wherever the
/// menu structure wants them.
@MainActor
final class TabsMenuController: NSObject, NSMenuDelegate, NSMenuItemValidation {
    private let model: BrowserModel
    private let recentlyClosedMenu = NSMenu(title: "Recently Closed")
    private let moveToWorkspaceMenu = NSMenu(title: "Move Tab to Workspace")
    private let switchWorkspaceMenu = NSMenu(title: "Switch to Workspace")

    init(model: BrowserModel) {
        self.model = model
        super.init()
        for menu in [recentlyClosedMenu, moveToWorkspaceMenu, switchWorkspaceMenu] { menu.delegate = self }
    }

    /// Appends the History menu items (with a separator first).
    func addHistoryItems(to menu: NSMenu) {
        menu.addItem(.separator())
        menu.addItem(item("Reopen Closed Tab", #selector(reopenClosedTab), "t", [.command, .shift]))
        menu.addItem(submenu(recentlyClosedMenu))
        menu.addItem(item("Reopen All Tabs from Last Session", #selector(restorePreviousSession)))
    }

    func makeTabMenu() -> NSMenu {
        let menu = NSMenu(title: "Tab")
        menu.addItem(item("Search Tabs…", #selector(searchTabs), "a", [.command, .shift]))
        menu.addItem(.separator())
        menu.addItem(item("Pin Tab", #selector(togglePin)))
        menu.addItem(item("Duplicate Tab", #selector(duplicateTab)))
        menu.addItem(item("Mute Tab", #selector(toggleMute)))
        menu.addItem(item("Unload Tab", #selector(unloadTab)))
        menu.addItem(item("Unload Background Tabs", #selector(unloadBackgroundTabs)))
        menu.addItem(.separator())
        menu.addItem(item("Add Tab to New Group", #selector(newGroup)))
        menu.addItem(item("Remove Tab from Group", #selector(removeFromGroup)))
        menu.addItem(item("Collapse Group", #selector(toggleGroupCollapsed)))
        menu.addItem(submenu(moveToWorkspaceMenu))
        menu.addItem(.separator())
        menu.addItem(item("New Workspace", #selector(newWorkspace), "n", [.command, .control]))
        menu.addItem(submenu(switchWorkspaceMenu))
        menu.addItem(item("Next Workspace", #selector(nextWorkspace), Self.key(NSRightArrowFunctionKey), [.command, .control]))
        menu.addItem(item("Previous Workspace", #selector(previousWorkspace), Self.key(NSLeftArrowFunctionKey), [.command, .control]))
        // ⌘1…⌘8 select that tab, ⌘9 the last one (Chrome and Safari's shortcuts). Hidden
        // items: they only carry the key equivalents.
        for number in 1...9 {
            let select = item(number == 9 ? "Select Last Tab" : "Select Tab \(number)", #selector(selectNumberedTab(_:)), "\(number)")
            select.tag = number
            select.isHidden = true
            select.allowsKeyEquivalentWhenHidden = true
            menu.addItem(select)
        }
        return menu
    }

    // MARK: Actions

    @objc private func reopenClosedTab() { model.reopenClosedTab() }
    @objc private func restorePreviousSession() { model.restorePreviousSession() }
    @objc private func reopenItem(_ sender: NSMenuItem) {
        if let id = sender.representedObject as? UUID { model.reopenClosed(id) }
    }
    @objc private func clearRecentlyClosed() { model.clearRecentlyClosed() }
    @objc private func searchTabs() { model.toggleTabSearch() }
    @objc private func togglePin() { if let tab = model.selectedTab { model.togglePin(tab) } }
    @objc private func duplicateTab() { if let tab = model.selectedTab { model.duplicate(tab) } }
    @objc private func toggleMute() { if let tab = model.selectedTab { model.toggleMute(tab) } }
    @objc private func unloadTab() { if let tab = model.selectedTab { model.unloadTab(tab) } }
    @objc private func unloadBackgroundTabs() { model.unloadBackgroundTabs() }
    @objc private func newGroup() { if let tab = model.selectedTab { model.createGroup(with: [tab]) } }
    @objc private func removeFromGroup() { if let tab = model.selectedTab { model.removeTabFromGroup(tab) } }
    @objc private func toggleGroupCollapsed() {
        if let id = model.selectedTab?.groupID { model.toggleGroupCollapsed(id) }
    }
    @objc private func newWorkspace() { model.createWorkspace() }
    @objc private func nextWorkspace() { model.selectAdjacentWorkspace(offset: 1) }
    @objc private func previousWorkspace() { model.selectAdjacentWorkspace(offset: -1) }
    @objc private func selectWorkspace(_ sender: NSMenuItem) {
        if let id = sender.representedObject as? UUID { model.selectWorkspace(id) }
    }
    @objc private func moveToWorkspace(_ sender: NSMenuItem) {
        guard let tab = model.selectedTab, let id = sender.representedObject as? UUID else { return }
        model.moveTab(tab, toWorkspace: id)
    }
    @objc private func moveToNewWorkspace() {
        guard let tab = model.selectedTab else { return }
        let workspace = model.createWorkspace(switchTo: false)
        model.moveTab(tab, toWorkspace: workspace.id)
    }
    @objc private func selectNumberedTab(_ sender: NSMenuItem) { model.selectTab(number: sender.tag) }

    // MARK: Validation and dynamic menus

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        let tab = model.selectedTab
        switch menuItem.action {
        case #selector(reopenClosedTab):
            let latest = model.recentlyClosedItems.first
            menuItem.title = switch latest?.kind {
            case .group: "Reopen Closed Group"
            case .workspace: "Reopen Closed Workspace"
            default: "Reopen Closed Tab"
            }
            return latest != nil
        case #selector(restorePreviousSession):
            return model.tabsFeature.previousSession != nil
        case #selector(togglePin):
            menuItem.title = tab?.isPinned == true ? "Unpin Tab" : "Pin Tab"
            return tab != nil
        case #selector(toggleMute):
            menuItem.title = tab?.isMuted == true ? "Unmute Tab" : "Mute Tab"
            return tab != nil
        case #selector(duplicateTab), #selector(newGroup):
            return tab != nil
        case #selector(unloadTab):
            guard let tab else { return false }
            return !tab.isAsleep && !tab.isStartPage && tab.internalPage == nil && model.visibleTabs.count > 1
        case #selector(removeFromGroup):
            return tab?.groupID != nil
        case #selector(toggleGroupCollapsed):
            let group = model.group(withID: tab?.groupID)
            menuItem.title = group?.isCollapsed == true ? "Expand Group" : "Collapse Group"
            return group != nil
        case #selector(nextWorkspace), #selector(previousWorkspace):
            return model.workspaces.count > 1
        case #selector(selectWorkspace(_:)):
            menuItem.state = (menuItem.representedObject as? UUID) == model.selectedWorkspaceID ? .on : .off
            return true
        case #selector(moveToWorkspace(_:)), #selector(moveToNewWorkspace):
            return tab != nil
        default:
            return true
        }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        if menu === recentlyClosedMenu {
            let items = model.recentlyClosedItems
            if items.isEmpty {
                let empty = NSMenuItem(title: "No Recently Closed Tabs", action: nil, keyEquivalent: "")
                empty.isEnabled = false
                menu.addItem(empty)
                return
            }
            for closed in items {
                let entry = item(closed.title, #selector(reopenItem(_:)))
                entry.representedObject = closed.id
                entry.image = NSImage(
                    systemSymbolName: closed.kind == .tab ? "doc" : closed.kind == .group ? "folder" : "square.stack",
                    accessibilityDescription: nil)
                entry.toolTip = closed.tabs.first?.url
                menu.addItem(entry)
            }
            menu.addItem(.separator())
            menu.addItem(item("Clear Recently Closed", #selector(clearRecentlyClosed)))
        } else if menu === switchWorkspaceMenu {
            for workspace in model.workspaces {
                let entry = item(workspace.name, #selector(selectWorkspace(_:)))
                entry.representedObject = workspace.id
                entry.image = NSImage(systemSymbolName: workspace.symbol, accessibilityDescription: nil)
                menu.addItem(entry)
            }
        } else if menu === moveToWorkspaceMenu {
            let current = model.selectedTab.map(model.workspaceID(of:))
            for workspace in model.workspaces where workspace.id != current {
                let entry = item(workspace.name, #selector(moveToWorkspace(_:)))
                entry.representedObject = workspace.id
                entry.image = NSImage(systemSymbolName: workspace.symbol, accessibilityDescription: nil)
                menu.addItem(entry)
            }
            if !menu.items.isEmpty { menu.addItem(.separator()) }
            menu.addItem(item("New Workspace", #selector(moveToNewWorkspace)))
        }
    }

    // MARK: Helpers

    private func item(
        _ title: String, _ action: Selector, _ key: String = "", _ modifiers: NSEvent.ModifierFlags = [.command]
    ) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.keyEquivalentModifierMask = modifiers
        item.target = self
        return item
    }

    private func submenu(_ menu: NSMenu) -> NSMenuItem {
        let item = NSMenuItem(title: menu.title, action: nil, keyEquivalent: "")
        item.submenu = menu
        return item
    }

    private static func key(_ functionKey: Int) -> String {
        String(Character(UnicodeScalar(UInt16(functionKey))!))
    }
}
