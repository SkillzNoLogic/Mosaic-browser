import Foundation
import MosaicCore

/// Shell actions built only from existing model members. Owner: **shell**.
extension BrowserModel {
    /// Tabs shown in the tab strip / sidebar: those of the selected workspace (TabsBridge).
    var displayedTabs: [BrowserTab] { shellVisibleTabs }

    var displayedPinnedTabs: [BrowserTab] { displayedTabs.filter(\.isPinned) }
    var displayedUnpinnedTabs: [BrowserTab] { displayedTabs.filter { !$0.isPinned } }

    /// New tab as the user configured it (New Tab page or homepage).
    func openNewTabFromShell() {
        if shellSettings.newTabPage == .homepage, let url = shellSettings.homepageURL(searchEngine: searchEngine) {
            newTab(url: url)
        } else {
            newTab()
        }
    }

    /// Home button / ⌘⇧H: the homepage in this tab, or a New Tab page when none is set.
    func goHome() {
        if let url = shellSettings.homepageURL(searchEngine: searchEngine) {
            if let tab = selectedTab {
                isEditingAddress = false
                load(url, in: tab)
                focusContent()
            } else {
                newTab(url: url)
            }
        } else if selectedTab?.isStartPage != true {
            newTab()
        }
    }

    /// The first tab at launch, according to Settings › General › Startup.
    func applyStartupPage() {
        guard shellSettings.startupPage == .homepage,
              let url = shellSettings.homepageURL(searchEngine: searchEngine),
              let tab = selectedTab, tab.isStartPage, tabs.count == 1 else { return }
        load(url, in: tab)
    }

    /// Opens settings at the remembered (or given) category.
    func showSettings(_ category: SettingsCategory? = nil) {
        let target = category ?? shellSettings.lastSettingsCategory
        shellSettings.lastSettingsCategory = target
        openSettings(target)
    }

    /// Esc / close button: back to the page; a Mosaic preview that was not applied ends.
    func dismissSettings() {
        mosaics.endPreview()
        closeSettings()
    }

    func toggleSettings() {
        if settingsCategory == nil { showSettings() } else { dismissSettings() }
    }

    /// The bookmark star's state for the selected tab.
    var selectedTabIsBookmarked: Bool {
        guard let tab = selectedTab, tab.isBookmarkable else { return false }
        return library.isBookmarked(tab.url)
    }

    func toggleBookmarkForSelectedTab() {
        guard let tab = selectedTab, tab.isBookmarkable else { return }
        library.toggleBookmark(url: tab.url, title: tab.displayTitle)
        objectWillChange.send()
    }

    /// Shows or hides the sidebar (vertical tabs, or Safari's hidden sidebar).
    func toggleSidebar() {
        let layout = mosaics.effectiveLayout
        if layout.tabPlacement.isVertical {
            switch layout.sidebar {
            case .shown: break
            case .collapsible: shellSettings.sidebarCollapsed.toggle()
            case .autoHide: shellSettings.sidebarPeek.toggle()
            }
        } else if layout.contains(.sidebarToggle) {
            shellSettings.sidebarOpen.toggle()
        }
    }

    /// Whether the sidebar is part of the layout right now (not counting an auto-hide peek).
    var isSidebarVisible: Bool {
        let layout = mosaics.effectiveLayout
        if layout.tabPlacement.isVertical {
            switch layout.sidebar {
            case .shown: return true
            case .collapsible: return !shellSettings.sidebarCollapsed
            case .autoHide: return false
            }
        }
        return layout.contains(.sidebarToggle) && shellSettings.sidebarOpen
    }

    /// Applies a preset from a menu or the command line.
    func applyMosaic(_ preset: MosaicPresetID) {
        mosaics.apply(preset)
        notice = "Mosaic: \(mosaics.layout.name)"
    }
}

extension BrowserTab {
    /// Web pages can be bookmarked; the New Tab page and Mosaic's own pages cannot.
    var isBookmarkable: Bool { !isStartPage && internalPage == nil && failure == nil }
}
