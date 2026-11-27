import Foundation

/// Words that find a settings category from the search field: its title plus the labels of
/// its settings. Owners of other categories may add their labels here (additive edits).
enum SettingsSearchIndex {
    static func labels(for category: SettingsCategory) -> [String] {
        switch category {
        // shell
        case .mosaics:
            ["Preset", "Zen / Arc", "Chrome", "Opera", "Opera GX", "Safari", "Preview", "Restore Defaults",
             "Tab placement", "Vertical tabs", "Sidebar", "Tab style", "Address bar", "Bookmarks bar",
             "Appearance", "Light", "Dark", "Theme", "Accent colour", "Density", "Corner radius",
             "Page inset", "Liquid Glass", "Background", "Gradient", "Glow", "Layout",
             // canvas
             "Customise Layout", "Toolbar buttons", "Drag and drop", "Gradient Studio", "New Tab background",
             "Workspace background", "Configurations", "Import layout", "Export layout"]
        case .general:
            ["Startup", "Homepage", "Home button", "New tabs open with", "New Tab page"]
        case .accessibility:
            ["Reduce motion", "Reduce transparency", "Text size", "Larger text", "Focus rings",
             "Keyboard navigation", "Tab close buttons", "VoiceOver"]
        // other areas: the obvious words until their owners add their own labels
        case .engine: ["Rendering engine", "Mozilla", "Gecko", "Chromium", "Blink", "Default engine", "Firefox"]
        case .tabs: ["Tabs", "Workspaces", "Pinned tabs", "Tab groups", "Session restore", "Recently closed"]
        case .search: ["Search engine", "Suggestions", "Google", "DuckDuckGo", "Bing", "Brave", "Kagi"]
        case .privacy: ["Content blocker", "Ad blocker", "Trackers", "Cookies", "Site data", "Permissions",
                        "HTTPS", "Clear browsing data", "Pop-ups"]
        case .passwords: ["Passwords", "Autofill", "Keychain", "Password generator"]
        case .downloads: ["Downloads", "Download folder", "Ask where to save", "Free Download Manager"]
        case .extensions: ["Extensions", "Chrome Web Store", "Add-ons", "crx"]
        case .shortcuts: ["Keyboard shortcuts", "Command palette", "Gestures"]
        case .languages: ["Languages", "Spelling", "Translate"]
        case .performance: ["Performance", "Memory", "Sleeping tabs", "Unload tabs"]
        case .about: ["About", "Version", "Updates", "Licences"]
        }
    }

    struct Match: Identifiable, Hashable {
        let category: SettingsCategory
        /// Setting labels that matched (empty when only the title did).
        let labels: [String]
        var id: SettingsCategory { category }
    }

    /// Categories matching `query` (every word must appear in the title or one label),
    /// in category order.
    static func search(_ query: String) -> [Match] {
        let words = query.lowercased().split(whereSeparator: \.isWhitespace).map(String.init)
        guard !words.isEmpty else { return SettingsCategory.allCases.map { Match(category: $0, labels: []) } }
        return SettingsCategory.allCases.compactMap { category in
            let title = category.title.lowercased()
            let labels = labels(for: category)
            let matchingLabels = labels.filter { label in
                let lower = label.lowercased()
                return words.allSatisfy { lower.contains($0) || title.contains($0) }
            }
            let titleMatches = words.allSatisfy { title.contains($0) }
            guard titleMatches || !matchingLabels.isEmpty else { return nil }
            return Match(category: category, labels: titleMatches && matchingLabels.count == labels.count ? [] : matchingLabels)
        }
    }
}
