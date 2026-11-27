import AppKit
import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// Library actions shared by the menus, the bookmarks bar and the pages.
@MainActor
enum LibraryCommands {
    /// Mosaic's browser window.
    static var window: NSWindow? {
        NSApp.windows.first { $0.title == "Mosaic" && $0.isVisible && !$0.isSheet }
            ?? NSApp.mainWindow
    }

    /// ⌘D: bookmarks the current page (in the folder used last) and shows the editor so it
    /// can be renamed or filed; if it is already bookmarked, just shows the editor.
    static func bookmarkCurrentPage(model: BrowserModel, showEditor: Bool = true) {
        guard let tab = model.selectedTab, LibraryURL.isRecordable(tab.url) else {
            model.notice = "Only web pages can be bookmarked."
            return
        }
        let library = model.library
        let existing = library.store.bookmarks(forURL: tab.url).first
        let bookmark = existing ?? library.addBookmark(url: tab.url, title: tab.displayTitle)
        guard let bookmark else { return }
        let folder = library.bookmark(id: bookmark.parentID ?? "")?.displayTitle ?? "Bookmarks"
        if existing == nil { model.notice = "Bookmarked in \(folder)" }
        guard showEditor, model.allowsFocusChanges else { return }
        LibrarySheet.present(on: window) { close in
            BookmarkEditor(library: library, bookmarkID: bookmark.id,
                           heading: existing == nil ? "Bookmark Added" : "Edit Bookmark", onDone: close)
        }
    }

    static func addCurrentPageToReadingList(model: BrowserModel) {
        guard let tab = model.selectedTab, LibraryURL.isRecordable(tab.url) else {
            model.notice = "Only web pages can be added to the Reading List."
            return
        }
        model.library.addToReadingList(url: tab.url, title: tab.displayTitle)
        model.notice = "Added to Reading List"
    }

    static func importBookmarks(model: BrowserModel) {
        let panel = NSOpenPanel()
        panel.title = "Import Bookmarks"
        panel.message = "Choose a bookmarks file exported from any browser (HTML), or Chrome’s Bookmarks file."
        panel.prompt = "Import"
        panel.allowedContentTypes = [.html, .json, .data]
        panel.allowsMultipleSelection = false
        let handler: (NSApplication.ModalResponse) -> Void = { response in
            guard response == .OK, let url = panel.url else { return }
            importBookmarks(from: url, model: model)
        }
        if let window { panel.beginSheetModal(for: window, completionHandler: handler) } else { handler(panel.runModal()) }
    }

    static func importBookmarks(from file: URL, model: BrowserModel) {
        do {
            let count = try model.library.importBookmarks(from: file)
            model.notice = count == 0 ? "No new bookmarks to import"
                : "Imported \(count) bookmark\(count == 1 ? "" : "s")"
        } catch {
            model.notice = "Could not import bookmarks: \(error.localizedDescription)"
        }
    }

    static func importFromChrome(model: BrowserModel) {
        guard let file = model.library.chromeBookmarksFile else {
            model.notice = "Google Chrome’s bookmarks were not found."
            return
        }
        importBookmarks(from: file, model: model)
    }

    static func exportBookmarks(model: BrowserModel) {
        let panel = NSSavePanel()
        panel.title = "Export Bookmarks"
        panel.nameFieldStringValue = "Mosaic Bookmarks.html"
        panel.allowedContentTypes = [.html]
        let handler: (NSApplication.ModalResponse) -> Void = { response in
            guard response == .OK, let url = panel.url else { return }
            do {
                try model.library.exportBookmarks(to: url)
                model.notice = "Bookmarks exported"
            } catch {
                model.notice = "Could not export bookmarks: \(error.localizedDescription)"
            }
        }
        if let window { panel.beginSheetModal(for: window, completionHandler: handler) } else { handler(panel.runModal()) }
    }
}

/// The Bookmarks menu, and the library's items in the History menu. `AppDelegate` creates
/// one and adds them additively (see its `library` comments).
@MainActor
final class LibraryMenus: NSObject, NSMenuDelegate, NSMenuItemValidation {
    private let model: BrowserModel
    private weak var historyMenu: NSMenu?
    private weak var bookmarksMenu: NSMenu?
    /// Items before this index are fixed; the rest are rebuilt each time the menu opens.
    private var historyFixedCount = 0
    private var bookmarksFixedCount = 0

    init(model: BrowserModel) {
        self.model = model
    }

    /// Adds Show All History and the recently visited pages to the History menu.
    func extendHistoryMenu(_ menu: NSMenu) {
        menu.addItem(.separator())
        menu.addItem(item("Show All History", #selector(showHistory), "y"))
        menu.addItem(.separator())
        let header = NSMenuItem(title: "Recently Visited", action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        historyFixedCount = menu.items.count
        historyMenu = menu
        menu.delegate = self
    }

    func makeBookmarksMenu() -> NSMenu {
        let menu = NSMenu(title: "Bookmarks")
        menu.addItem(item("Bookmark This Page…", #selector(bookmarkPage), "d"))
        menu.addItem(item("Add to Reading List", #selector(addToReadingList), "d", [.command, .shift]))
        menu.addItem(.separator())
        menu.addItem(item("Show Bookmarks", #selector(showBookmarks), "b", [.command, .option]))
        menu.addItem(item("Show Reading List", #selector(showReadingList), ""))
        menu.addItem(item("Show Bookmarks Bar", #selector(toggleBar), "b", [.command, .shift]))
        menu.addItem(.separator())
        menu.addItem(item("Import Bookmarks…", #selector(importBookmarks), ""))
        menu.addItem(item("Import from Google Chrome", #selector(importChrome), ""))
        menu.addItem(item("Export Bookmarks…", #selector(exportBookmarks), ""))
        bookmarksFixedCount = menu.items.count
        bookmarksMenu = menu
        menu.delegate = self
        return menu
    }

    private func item(_ title: String, _ action: Selector, _ key: String,
                      _ modifiers: NSEvent.ModifierFlags = [.command]) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.keyEquivalentModifierMask = modifiers
        item.target = self
        return item
    }

    // MARK: Dynamic parts

    func menuNeedsUpdate(_ menu: NSMenu) {
        if menu === historyMenu {
            trim(menu, to: historyFixedCount)
            let recent = model.library.recentHistory(limit: 12)
            if recent.isEmpty {
                let empty = NSMenuItem(title: "No History", action: nil, keyEquivalent: "")
                empty.isEnabled = false
                menu.addItem(empty)
            }
            for entry in recent { menu.addItem(pageItem(title: entry.displayTitle, url: entry.url)) }
        } else if menu === bookmarksMenu {
            trim(menu, to: bookmarksFixedCount)
            let library = model.library
            let bar = library.bookmarksBar
            let other = library.children(of: Bookmark.otherID)
            if !bar.isEmpty || !other.isEmpty { menu.addItem(.separator()) }
            for bookmark in bar { menu.addItem(menuItem(for: bookmark)) }
            if !other.isEmpty, let folder = library.bookmark(id: Bookmark.otherID) {
                menu.addItem(.separator())
                menu.addItem(menuItem(for: folder))
            }
        }
    }

    private func trim(_ menu: NSMenu, to count: Int) {
        while menu.items.count > count { menu.removeItem(at: menu.items.count - 1) }
    }

    private func menuItem(for bookmark: Bookmark) -> NSMenuItem {
        guard bookmark.isFolder else { return pageItem(title: bookmark.displayTitle, url: bookmark.url ?? "") }
        let item = NSMenuItem(title: bookmark.displayTitle, action: nil, keyEquivalent: "")
        item.image = NSImage(systemSymbolName: "folder", accessibilityDescription: nil)
        let submenu = NSMenu(title: bookmark.displayTitle)
        let children = model.library.children(of: bookmark.id)
        if children.isEmpty {
            let empty = NSMenuItem(title: "Empty", action: nil, keyEquivalent: "")
            empty.isEnabled = false
            submenu.addItem(empty)
        }
        for child in children { submenu.addItem(menuItem(for: child)) }
        item.submenu = submenu
        return item
    }

    private func pageItem(title: String, url: String) -> NSMenuItem {
        let shortTitle = title.count > 60 ? String(title.prefix(59)) + "…" : title
        let item = NSMenuItem(title: shortTitle, action: #selector(openPage(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = url
        item.toolTip = url
        return item
    }

    // MARK: Actions

    @objc private func openPage(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? String else { return }
        libraryOpen(url, in: model)
    }

    @objc private func showHistory() { model.openInternalPage(.history) }
    @objc private func showBookmarks() { model.openInternalPage(.bookmarks) }
    @objc private func showReadingList() { model.openInternalPage(.readingList) }
    @objc private func bookmarkPage() { LibraryCommands.bookmarkCurrentPage(model: model) }
    @objc private func addToReadingList() { LibraryCommands.addCurrentPageToReadingList(model: model) }
    @objc private func toggleBar() { model.library.showsBookmarksBar.toggle() }
    @objc private func importBookmarks() { LibraryCommands.importBookmarks(model: model) }
    @objc private func importChrome() { LibraryCommands.importFromChrome(model: model) }
    @objc private func exportBookmarks() { LibraryCommands.exportBookmarks(model: model) }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        let pageIsWeb = model.selectedTab.map { LibraryURL.isRecordable($0.url) } ?? false
        switch menuItem.action {
        case #selector(bookmarkPage):
            menuItem.title = (model.selectedTab.map { model.library.isBookmarked($0.url) } ?? false)
                ? "Edit Bookmark…" : "Bookmark This Page…"
            return pageIsWeb
        case #selector(addToReadingList):
            return pageIsWeb
        case #selector(toggleBar):
            menuItem.title = model.library.showsBookmarksBar ? "Hide Bookmarks Bar" : "Show Bookmarks Bar"
            return true
        case #selector(importChrome):
            menuItem.isHidden = model.library.chromeBookmarksFile == nil
            return true
        default:
            return true
        }
    }
}
