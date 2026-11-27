import AppKit
import MosaicCore

/// The palette actions for everything the browser model can do today. Actions owned by
/// other areas (tab groups, workspaces, downloads, find, zoom…) are registered by those
/// areas, or added here by the integrator once their API exists (see docs in
/// `CommandPalette.register`).
@MainActor
enum BuiltInPaletteActions {
    static func actions(_ model: BrowserModel) -> [PaletteAction] {
        let shortcuts = model.productivity.shortcuts
        func key(_ selector: String) -> KeyCombo? { shortcuts.combo(forSelector: selector) }
        let tab = model.selectedTab
        let isWebPage = tab.map { !$0.isStartPage && $0.internalPage == nil } ?? false
        var items: [PaletteAction] = []

        // Tabs
        items.append(PaletteAction(id: "tab.new", title: "New Tab", keywords: ["open", "blank"],
                                   symbol: "plus.square", shortcut: key("newTab"), boost: 3) { model.newTab() })
        if let tab {
            items.append(PaletteAction(id: "tab.close", title: "Close Tab", subtitle: tab.displayTitle,
                                       symbol: "xmark.square", shortcut: key("closeTab")) { model.close(tab) })
            if !tab.isStartPage {
                items.append(PaletteAction(id: "tab.duplicate", title: "Duplicate Tab", keywords: ["copy", "clone"],
                                           symbol: "plus.square.on.square") { model.newTab(url: tab.url, engine: tab.engine) })
            }
        }
        if model.tabs.count > 1 {
            items.append(PaletteAction(id: "tab.next", title: "Show Next Tab", keywords: ["switch"],
                                       symbol: "arrow.right.square", shortcut: key("nextTab")) { model.selectAdjacentTab(offset: 1) })
            items.append(PaletteAction(id: "tab.previous", title: "Show Previous Tab", keywords: ["switch"],
                                       symbol: "arrow.left.square", shortcut: key("previousTab")) { model.selectAdjacentTab(offset: -1) })
        }

        // Navigation
        items.append(PaletteAction(id: "nav.location", title: "Open Location…", keywords: ["address", "url", "go to"],
                                   symbol: "link", shortcut: key("focusAddress")) { model.focusAddressBar(activating: true) })
        if let tab, isWebPage {
            items.append(PaletteAction(id: "nav.reload", title: "Reload Page", keywords: ["refresh"],
                                       symbol: "arrow.clockwise", shortcut: key("reload")) { model.reload() })
            if tab.isLoading {
                items.append(PaletteAction(id: "nav.stop", title: "Stop Loading", symbol: "xmark",
                                           shortcut: key("stopLoading")) { model.stopLoading() })
            }
            if tab.canGoBack {
                items.append(PaletteAction(id: "nav.back", title: "Back", keywords: ["previous page"], symbol: "chevron.left",
                                           shortcut: key("goBack")) { model.goBack() })
            }
            if tab.canGoForward {
                items.append(PaletteAction(id: "nav.forward", title: "Forward", keywords: ["next page"], symbol: "chevron.right",
                                           shortcut: key("goForward")) { model.goForward() })
            }
            items.append(PaletteAction(id: "page.copyAddress", title: "Copy Page Address", subtitle: tab.url,
                                       keywords: ["url", "link", "clipboard"], symbol: "doc.on.doc") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(tab.url, forType: .string)
                model.notice = "Address copied"
            })
        }

        // Reader view
        if let tab, isWebPage {
            let active = tab.isReaderActive
            items.append(PaletteAction(
                id: "reader.toggle", title: active ? "Leave Reader View" : "Show Reader View",
                subtitle: active || tab.isReaderAvailable ? nil : "This page may not be an article",
                keywords: ["reader mode", "article", "read", "distraction"], symbol: active ? "doc.plaintext.fill" : "doc.plaintext",
                shortcut: key("toggleReader"), boost: tab.isReaderAvailable ? 2 : 0
            ) { model.toggleReaderMode() })
            if active {
                items += readerSettingActions(model)
            }
        }

        // Engines and search
        if let tab {
            let other = tab.engine.other
            items.append(PaletteAction(
                id: "engine.switch", title: "Switch Tab to \(other.displayName)",
                subtitle: model.chromiumAvailable || other == .gecko ? nil : "Chromium is not bundled in this build",
                keywords: ["engine", "rendering", other.engineName, "gecko", "blink", "firefox", "chrome"],
                symbol: "arrow.triangle.2.circlepath", shortcut: key("toggleEngine")
            ) { model.setEngine(other) })
        }
        items.append(PaletteAction(id: "search.next", title: "Next Search Engine", subtitle: "Now: \(model.searchEngine.name)",
                                   keywords: ["cycle"], symbol: "magnifyingglass", shortcut: key("cycleSearchEngine")) {
            model.cycleSearchEngine()
        })
        for engine in SearchEngine.builtIn where engine != model.searchEngine {
            items.append(PaletteAction(id: "search.use.\(engine.id)", title: "Search with \(engine.name)",
                                       keywords: ["search engine", "default search"], symbol: "magnifyingglass") {
                model.searchEngine = engine
                model.notice = "Search engine: \(engine.name)"
            })
        }

        // Content blocker
        if model.blocker.status != .unavailable {
            let on = model.blocker.isEnabled
            items.append(PaletteAction(id: "blocker.toggle", title: on ? "Turn Off Content Blocker" : "Turn On Content Blocker",
                                       keywords: ["ads", "trackers", "adblock", "ublock", "shield"], symbol: on ? "shield.slash" : "shield",
                                       shortcut: key("toggleBlocker")) { model.setBlockerEnabled(!on) })
            if let tab, isWebPage, let site = model.blocker.site(ofPage: tab.url) {
                let filtering = model.blocker.isFiltering(pageURL: tab.url)
                items.append(PaletteAction(id: "blocker.site", title: filtering ? "Allow Ads on \(site)" : "Block Ads on \(site)",
                                           keywords: ["blocker", "trackers", "exception", "allowlist"],
                                           symbol: filtering ? "shield.lefthalf.filled.slash" : "shield.lefthalf.filled") {
                    model.setBlocking(!filtering, onSiteOf: tab)
                })
            }
            items.append(PaletteAction(id: "blocker.panel", title: "Show Blocker Panel", keywords: ["blocked", "ads", "trackers"],
                                       symbol: "shield.lefthalf.filled") { model.showBlocker = true })
        }

        // Extensions
        items.append(PaletteAction(id: "extensions.show", title: "Extensions…", keywords: ["add-ons", "chrome web store"],
                                   symbol: "puzzlepiece.extension", shortcut: key("showExtensions")) { model.showExtensions = true })

        // Library pages and bookmarks
        for page in InternalPage.allCases {
            items.append(PaletteAction(id: "page.\(page.rawValue)", title: "Open \(page.title)", keywords: ["show", "library"],
                                       symbol: symbol(for: page)) { model.openInternalPage(page) })
        }
        if let tab, isWebPage {
            let bookmarked = model.library.isBookmarked(tab.url)
            items.append(PaletteAction(id: "bookmark.toggle", title: bookmarked ? "Remove Bookmark" : "Bookmark This Page",
                                       subtitle: tab.displayTitle, keywords: ["star", "favourite", "favorite", "save"],
                                       symbol: bookmarked ? "star.slash" : "star") {
                model.library.toggleBookmark(url: tab.url, title: tab.title)
            })
            let dial = model.productivity.speedDial
            if !dial.dial.contains(url: tab.url), SpeedDial.normalizedURL(tab.url) != nil {
                items.append(PaletteAction(id: "speedDial.add", title: "Add Page to Speed Dial", subtitle: tab.displayTitle,
                                           keywords: ["new tab", "shortcut", "pin", "favourite"], symbol: "square.grid.3x3.square") {
                    model.notice = (try? dial.add(title: tab.title, url: tab.url)) != nil
                        ? "Added to Speed Dial" : "Could not add this page to Speed Dial"
                })
            }
        }

        // Settings shortcuts beyond the category list
        items.append(PaletteAction(id: "settings.open", title: "Settings…", keywords: ["preferences", "options"],
                                   symbol: "gearshape") {
            model.openSettings(model.settingsCategory ?? .general)
        })
        items.append(PaletteAction(id: "shortcuts.customise", title: "Customise Keyboard Shortcuts…",
                                   keywords: ["keys", "hotkeys", "bindings", "settings"], symbol: "command") {
            model.openSettings(.shortcuts)
        })
        return items
    }

    private static func readerSettingActions(_ model: BrowserModel) -> [PaletteAction] {
        let reader = model.productivity.reader
        let s = reader.settings
        func change(_ edit: @escaping (inout ReaderSettings) -> Void) -> @MainActor () -> Void {
            { var copy = reader.settings; edit(&copy); reader.update(copy.clamped) }
        }
        var items: [PaletteAction] = []
        if s.fontSize < 9 {
            items.append(PaletteAction(id: "reader.larger", title: "Reader: Larger Text", keywords: ["font size", "bigger"],
                                       symbol: "textformat.size.larger", boost: -8, perform: change { $0.fontSize += 1 }))
        }
        if s.fontSize > 1 {
            items.append(PaletteAction(id: "reader.smaller", title: "Reader: Smaller Text", keywords: ["font size"],
                                       symbol: "textformat.size.smaller", boost: -8, perform: change { $0.fontSize -= 1 }))
        }
        let otherFont: ReaderSettings.Font = s.font == .serif ? .sans : .serif
        items.append(PaletteAction(id: "reader.font", title: "Reader: \(otherFont.title) Font", keywords: ["typeface"],
                                   symbol: "textformat", boost: -8, perform: change { $0.font = otherFont }))
        for theme in ReaderSettings.Theme.allCases where theme != s.theme {
            items.append(PaletteAction(id: "reader.theme.\(theme.rawValue)", title: "Reader: \(theme.title) Background",
                                       keywords: ["colour", "color", "theme"], symbol: "circle.lefthalf.filled", boost: -8,
                                       perform: change { $0.theme = theme }))
        }
        return items
    }

    private static func symbol(for page: InternalPage) -> String {
        switch page {
        case .bookmarks: "book"
        case .history: "clock"
        case .downloads: "arrow.down.circle"
        case .readingList: "eyeglasses"
        }
    }
}
