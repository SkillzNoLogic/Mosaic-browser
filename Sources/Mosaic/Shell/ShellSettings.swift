import Foundation
import MosaicCore
import Observation

/// General and Accessibility preferences, plus window state the shell remembers.
/// Owner: **shell**. Stored in `AppPaths.defaults` (so an isolated profile keeps its own).
@MainActor
@Observable
final class ShellSettings {
    enum StartupPage: String, CaseIterable, Identifiable {
        case startPage, homepage
        var id: String { rawValue }
        var title: String { self == .startPage ? "New Tab page" : "Homepage" }
    }

    enum NewTabPage: String, CaseIterable, Identifiable {
        case startPage, homepage
        var id: String { rawValue }
        var title: String { self == .startPage ? "New Tab page" : "Homepage" }
    }

    /// An override of a system accessibility setting.
    enum Override: String, CaseIterable, Identifiable {
        case system, on, off
        var id: String { rawValue }
        var title: String {
            switch self {
            case .system: "Use System Setting"
            case .on: "On"
            case .off: "Off"
            }
        }

        func resolve(system: Bool) -> Bool {
            switch self {
            case .system: system
            case .on: true
            case .off: false
            }
        }
    }

    enum TextSize: String, CaseIterable, Identifiable {
        case standard, large, larger
        var id: String { rawValue }
        var title: String { rawValue.capitalized }
        /// Multiplier for the shell's text and control sizes.
        var scale: CGFloat {
            switch self {
            case .standard: 1
            case .large: 1.12
            case .larger: 1.25
            }
        }
    }

    // MARK: General

    /// Empty means the New Tab page.
    var homepage: String { didSet { save(homepage, .homepage) } }
    var showsHomeButton: Bool { didSet { save(showsHomeButton, .showsHomeButton) } }
    var startupPage: StartupPage { didSet { save(startupPage.rawValue, .startupPage) } }
    var newTabPage: NewTabPage { didSet { save(newTabPage.rawValue, .newTabPage) } }

    // MARK: Accessibility

    var reduceMotion: Override { didSet { save(reduceMotion.rawValue, .reduceMotion) } }
    var reduceTransparency: Override { didSet { save(reduceTransparency.rawValue, .reduceTransparency) } }
    var textSize: TextSize { didSet { save(textSize.rawValue, .textSize) } }
    /// Draws a thick accent ring around the keyboard-focused control.
    var prominentFocusRings: Bool { didSet { save(prominentFocusRings, .prominentFocusRings) } }
    /// Tabs always show their close button (not only on hover).
    var alwaysShowTabCloseButtons: Bool { didSet { save(alwaysShowTabCloseButtons, .alwaysShowTabCloseButtons) } }

    // MARK: Window state

    /// The user hid the vertical-tabs sidebar (when it is collapsible).
    var sidebarCollapsed: Bool { didSet { save(sidebarCollapsed, .sidebarCollapsed) } }
    /// The user opened the sidebar of a horizontal-tabs layout (Safari's sidebar button).
    var sidebarOpen: Bool { didSet { save(sidebarOpen, .sidebarOpen) } }
    /// An auto-hiding sidebar is showing over the page (pointer at the edge, or ⌘⇧L). Not saved.
    var sidebarPeek = false
    /// Settings category to reopen with ⌘,.
    var lastSettingsCategory: SettingsCategory { didSet { save(lastSettingsCategory.rawValue, .lastSettingsCategory) } }

    private enum Key: String {
        case homepage, showsHomeButton, startupPage, newTabPage
        case reduceMotion, reduceTransparency, textSize, prominentFocusRings, alwaysShowTabCloseButtons
        case sidebarCollapsed, sidebarOpen, lastSettingsCategory
        var name: String { "shell." + rawValue }
    }

    @ObservationIgnored private let defaults: UserDefaults

    init(defaults: UserDefaults = AppPaths.defaults) {
        self.defaults = defaults
        func string(_ key: Key) -> String? { defaults.string(forKey: key.name) }
        func bool(_ key: Key, _ fallback: Bool) -> Bool { defaults.object(forKey: key.name) as? Bool ?? fallback }
        homepage = string(.homepage) ?? ""
        showsHomeButton = bool(.showsHomeButton, true)
        startupPage = string(.startupPage).flatMap(StartupPage.init) ?? .startPage
        newTabPage = string(.newTabPage).flatMap(NewTabPage.init) ?? .startPage
        reduceMotion = string(.reduceMotion).flatMap(Override.init) ?? .system
        reduceTransparency = string(.reduceTransparency).flatMap(Override.init) ?? .system
        textSize = string(.textSize).flatMap(TextSize.init) ?? .standard
        prominentFocusRings = bool(.prominentFocusRings, false)
        alwaysShowTabCloseButtons = bool(.alwaysShowTabCloseButtons, false)
        sidebarCollapsed = bool(.sidebarCollapsed, false)
        sidebarOpen = bool(.sidebarOpen, false)
        lastSettingsCategory = string(.lastSettingsCategory).flatMap(SettingsCategory.init) ?? .mosaics
    }

    private func save(_ value: Any, _ key: Key) { defaults.set(value, forKey: key.name) }

    /// The homepage as a loadable address, or nil when it is the New Tab page or not valid.
    func homepageURL(searchEngine: SearchEngine) -> String? {
        let trimmed = homepage.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, AddressResolver.isAddress(trimmed) else { return nil }
        return AddressResolver.resolve(trimmed, searchEngine: searchEngine)?.absoluteString
    }
}
