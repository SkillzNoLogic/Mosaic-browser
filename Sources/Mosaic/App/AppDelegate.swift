import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuItemValidation {
    private(set) var model: BrowserModel!
    private var window: NSWindow!
    private var workspaceObserver: NSObjectProtocol?
    private var automation: Automation?
    /// Tabs area: Tab menu and History items (Features/Tabs/TabsMenuController.swift).
    private var tabsMenu: TabsMenuController?
    private var libraryMenus: LibraryMenus?  // library: Bookmarks menu, History additions

    /// Builds the model, window and menus. Called before the event loop starts.
    func setUp(chromiumAvailable: Bool) {
        model = BrowserModel(chromiumAvailable: chromiumAvailable)
        NSApp.mainMenu = makeMainMenu()
        window = makeWindow()
        // No explicit activation: Launch Services decides whether Mosaic comes
        // to the front (it does for a normal launch, not for `open -g`).
        window.makeKeyAndOrderFront(nil)

        if CommandLine.arguments.contains(Automation.flag) {
            automation = Automation(model: model, window: window)
        }

        // When the user lands on the Gecko window (click, ⌘-Tab), tuck Mosaic's
        // window directly behind it so the two always travel as one.
        workspaceObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
        ) { [weak self] notification in
            let app = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            MainActor.assumeIsolated { self?.applicationActivated(app) }
        }
    }

    private func makeWindow() -> NSWindow {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1240, height: 800),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered, defer: false)
        window.title = "Mosaic"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        // Transparent wherever Mosaic draws nothing: that is what lets the docked
        // Gecko window show through, and receive clicks, in the content area.
        window.isOpaque = false
        window.backgroundColor = .clear
        window.minSize = NSSize(width: 720, height: 420)
        // A full-screen Space cannot host the companion Gecko window.
        window.collectionBehavior = [.fullScreenNone, .managed]
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.contentView = NSHostingView(rootView: BrowserView().environmentObject(model))
        window.center()
        window.setFrameAutosaveName("MosaicMainWindow")
        return window
    }

    // MARK: - Keeping Mosaic and Gecko together

    private func applicationActivated(_ app: NSRunningApplication?) {
        guard let app, model.isGeckoDocked, app.processIdentifier == model.gecko.processIdentifier,
              let geckoWindow = model.gecko.windowNumber()
        else { return }
        window.order(.below, relativeTo: geckoWindow)
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        bringGeckoAlongside()
    }

    /// If other apps' windows have come between Mosaic and Gecko, the content
    /// area would show them instead of the page. Gecko's window can only be
    /// raised by activating Gecko, so do that and take focus straight back.
    private func bringGeckoAlongside() {
        guard model.isGeckoDocked, window.isVisible, let geckoPID = model.gecko.processIdentifier else { return }
        let stack = GeckoEngine.onScreenWindows()
        guard let mine = stack.firstIndex(where: { $0.number == window.windowNumber }),
              let theirs = stack.firstIndex(where: { $0.pid == geckoPID && $0.area > 10_000 })
        else { return }
        let ownPID = getpid()
        let low = min(mine, theirs), high = max(mine, theirs)
        let separated = stack[(low + 1)..<high].contains { $0.pid != ownPID && $0.pid != geckoPID }
        guard separated else { return }
        model.gecko.activate()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
            NSApp.activate(ignoringOtherApps: true)
            self.window.makeKeyAndOrderFront(nil)
        }
    }

    // MARK: - Window delegate

    func windowDidMiniaturize(_ notification: Notification) { model.setWindowVisible(false) }
    func windowDidDeminiaturize(_ notification: Notification) { model.setWindowVisible(true) }
    func applicationDidHide(_ notification: Notification) { model.setWindowVisible(false) }
    func applicationDidUnhide(_ notification: Notification) { model.setWindowVisible(true) }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.terminate(nil)
        return false
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if window.isMiniaturized { window.deminiaturize(nil) }
        window.makeKeyAndOrderFront(nil)
        return true
    }

    /// Last chance to clean up; runs once however the app is quit.
    func prepareToQuit() {
        if let workspaceObserver { NSWorkspace.shared.notificationCenter.removeObserver(workspaceObserver) }
        model?.shutdown()
    }

    // MARK: - Menu

    private func makeMainMenu() -> NSMenu {
        let main = NSMenu()

        let app = NSMenu()
        app.addItem(withTitle: "About Mosaic", action: #selector(showAbout), keyEquivalent: "").target = self
        app.addItem(.separator())
        app.addItem(withTitle: "Hide Mosaic", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let hideOthers = app.addItem(
            withTitle: "Hide Others", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        app.addItem(.separator())
        app.addItem(withTitle: "Quit Mosaic", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        main.addItem(submenu: app, title: "Mosaic")

        let file = NSMenu(title: "File")
        file.addItem(item("New Tab", #selector(newTab), "t"))
        file.addItem(item("Open Location…", #selector(focusAddress), "l"))
        file.addItem(.separator())
        file.addItem(item("Close Tab", #selector(closeTab), "w"))
        main.addItem(submenu: file, title: "File")

        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        main.addItem(submenu: edit, title: "Edit")

        let view = NSMenu(title: "View")
        view.addItem(item("Reload Page", #selector(reload), "r"))
        view.addItem(item("Stop", #selector(stopLoading), "."))
        view.addItem(.separator())
        view.addItem(item("Switch Rendering Engine", #selector(toggleEngine), "e", [.command, .shift]))
        view.addItem(item("Next Search Engine", #selector(cycleSearchEngine), "s", [.command, .shift]))
        // ⇧⌘B is the bookmarks bar (as in Safari and Chrome); the blocker takes ⌥⇧⌘B.
        view.addItem(item("Block Ads and Trackers", #selector(toggleBlocker), "b", [.command, .shift, .option]))
        view.addItem(.separator())
        view.addItem(item("Extensions…", #selector(showExtensions), "x", [.command, .shift]))
        main.addItem(submenu: view, title: "View")

        let history = NSMenu(title: "History")
        history.addItem(item("Back", #selector(goBack), "["))
        history.addItem(item("Forward", #selector(goForward), "]"))
        // Tabs area: Reopen Closed Tab (⌘⇧T), Recently Closed, Reopen All Tabs from Last Session.
        let tabsMenu = TabsMenuController(model: model)
        self.tabsMenu = tabsMenu
        tabsMenu.addHistoryItems(to: history)
        main.addItem(submenu: history, title: "History")
        // library: Show All History, recently visited, the Bookmarks menu (Features/Library/LibraryMenus.swift)
        let libraryMenus = LibraryMenus(model: model)
        self.libraryMenus = libraryMenus
        libraryMenus.extendHistoryMenu(history)
        main.addItem(submenu: libraryMenus.makeBookmarksMenu(), title: "Bookmarks")

        // Tabs area: the Tab menu (search, pin, duplicate, mute, unload, groups, workspaces, ⌘1–9).
        main.addItem(submenu: tabsMenu.makeTabMenu(), title: "Tab")

        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowMenu.addItem(withTitle: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        windowMenu.addItem(.separator())
        windowMenu.addItem(item("Show Next Tab", #selector(nextTab), "}", [.command]))
        windowMenu.addItem(item("Show Previous Tab", #selector(previousTab), "{", [.command]))
        windowMenu.addItem(.separator()) // downloads area
        windowMenu.addItem(item("Downloads", #selector(showDownloads), "l", [.command, .option]))
        main.addItem(submenu: windowMenu, title: "Window")
        NSApp.windowsMenu = windowMenu

        return main
    }

    private func item(
        _ title: String, _ action: Selector, _ key: String, _ modifiers: NSEvent.ModifierFlags = [.command]
    ) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.keyEquivalentModifierMask = modifiers
        item.target = self
        return item
    }

    @objc private func newTab() { model.newTab() }
    @objc private func closeTab() { model.closeSelectedTab() }
    @objc private func focusAddress() { model.focusAddressBar() }
    @objc private func reload() { model.reload() }
    @objc private func stopLoading() { model.stopLoading() }
    @objc private func goBack() { model.goBack() }
    @objc private func goForward() { model.goForward() }
    @objc private func toggleEngine() { model.toggleEngine() }
    @objc private func cycleSearchEngine() { model.cycleSearchEngine() }
    @objc private func toggleBlocker() { model.toggleBlocker() }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == #selector(toggleBlocker) {
            menuItem.state = model.blocker.isEnabled ? .on : .off
            return model.blocker.status != .unavailable
        }
        return true
    }
    @objc private func showExtensions() { model.showExtensions = true }
    @objc private func nextTab() { model.selectAdjacentTab(offset: 1) }
    @objc private func previousTab() { model.selectAdjacentTab(offset: -1) }
    @objc private func showDownloads() { model.downloads.openDownloadsPage() } // downloads area

    @objc private func showAbout() {
        var engines = "Chromium \(MosaicCEF.chromiumVersion)" + (model.chromiumAvailable ? "" : " (not bundled)")
        if let runtime = model.gecko.runtime {
            engines += "\nGecko: \(runtime.name) \(runtime.version)"
        } else {
            engines += "\nGecko: not installed"
        }
        NSApp.orderFrontStandardAboutPanel(options: [
            .credits: NSAttributedString(
                string: engines,
                attributes: [.font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.secondaryLabelColor]),
        ])
    }
}

private extension NSMenu {
    func addItem(submenu: NSMenu, title: String) {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        submenu.title = title
        item.submenu = submenu
        addItem(item)
    }
}
