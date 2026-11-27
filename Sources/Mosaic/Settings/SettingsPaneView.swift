import SwiftUI

/// Maps a settings category to the page its owner provides. The settings container (owned
/// by the shell) shows this for the selected category.
struct SettingsPaneView: View {
    let category: SettingsCategory

    var body: some View {
        switch category {
        case .mosaics: MosaicsSettingsPane()
        case .general: GeneralSettingsPane()
        case .accessibility: AccessibilitySettingsPane()
        case .tabs: TabsSettingsPane()
        case .performance: PerformanceSettingsPane()
        case .search: SearchSettingsPane()
        case .privacy: PrivacySettingsPane()
        case .passwords: PasswordsSettingsPane()
        case .downloads: DownloadsSettingsPane()
        case .engine: EngineSettingsPane()
        case .extensions: ExtensionsSettingsPane()
        case .languages: LanguagesSettingsPane()
        case .about: AboutSettingsPane()
        case .shortcuts: ShortcutsSettingsPane()
        }
    }
}

/// Temporary page for a category whose owner has not delivered yet. Every use is removed
/// before release (tracked in `To Do.md`); it exists only so the app builds meanwhile.
struct SettingsPanePending: View {
    let category: SettingsCategory

    var body: some View {
        ContentUnavailableView(category.title, systemImage: category.symbol,
                               description: Text("This section is being built."))
    }
}
