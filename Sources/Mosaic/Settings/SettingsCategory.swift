import SwiftUI

/// The settings categories, shown inside the main window (never a separate window).
///
/// Appearance and customisation live in `mosaics`; there is deliberately no separate
/// Appearance category. Each category's page is a view owned by one feature area; see
/// `SettingsPaneView` and `docs/AGENTS.md`.
enum SettingsCategory: String, CaseIterable, Identifiable, Codable {
    case mosaics, engine, general, tabs, search, privacy, passwords, downloads, extensions
    case shortcuts, languages, accessibility, performance, about

    var id: String { rawValue }

    var title: String {
        switch self {
        case .mosaics: "Mosaics"
        case .engine: "Engine"
        case .general: "General"
        case .tabs: "Tabs & Workspaces"
        case .search: "Search"
        case .privacy: "Privacy & Security"
        case .passwords: "Passwords & Autofill"
        case .downloads: "Downloads"
        case .extensions: "Extensions"
        case .shortcuts: "Shortcuts & Gestures"
        case .languages: "Languages"
        case .accessibility: "Accessibility"
        case .performance: "Performance"
        case .about: "About & Updates"
        }
    }

    /// SF Symbol for the category list.
    var symbol: String {
        switch self {
        case .mosaics: "square.grid.2x2"
        case .engine: "engine.combustion"
        case .general: "gearshape"
        case .tabs: "square.on.square"
        case .search: "magnifyingglass"
        case .privacy: "hand.raised"
        case .passwords: "key"
        case .downloads: "arrow.down.circle"
        case .extensions: "puzzlepiece.extension"
        case .shortcuts: "command"
        case .languages: "globe"
        case .accessibility: "accessibility"
        case .performance: "gauge.with.dots.needle.67percent"
        case .about: "info.circle"
        }
    }
}
