import MosaicCore
import SwiftUI

/// Settings › General: startup, homepage, Home button, new tabs. Owner: **shell**.
/// (The default engine for new tabs lives in Settings › Engine, owned by the platform area.)
struct GeneralSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        @Bindable var settings = model.shellSettings
        Form {
            Section("Startup") {
                Picker("Mosaic opens with", selection: $settings.startupPage) {
                    ForEach(ShellSettings.StartupPage.allCases) { Text($0.title).tag($0) }
                }
                if settings.startupPage == .homepage, settings.homepageURL(searchEngine: model.searchEngine) == nil {
                    SettingsNote("Set a homepage below; until then Mosaic opens with the New Tab page.")
                }
            }

            Section("Homepage") {
                TextField("Homepage", text: $settings.homepage, prompt: Text("New Tab page"))
                    .textContentType(.URL)
                    .accessibilityLabel("Homepage address")
                HStack {
                    Button("Use Current Page") {
                        if let tab = model.selectedTab, tab.isBookmarkable { settings.homepage = tab.url }
                    }
                    .disabled(!(model.selectedTab?.isBookmarkable ?? false))
                    Button("Use New Tab Page") { settings.homepage = "" }
                        .disabled(settings.homepage.isEmpty)
                    Spacer()
                }
                if !settings.homepage.isEmpty, settings.homepageURL(searchEngine: model.searchEngine) == nil {
                    Label("That doesn’t look like a web address.", systemImage: "exclamationmark.triangle")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
                Toggle("Show Home button", isOn: $settings.showsHomeButton)
                SettingsNote("Home (⌘⇧H) opens the homepage in the current tab, or a New Tab page if none is set. The button appears where the Mosaic places it.")
            }

            Section("New Tabs") {
                Picker("New tabs open with", selection: $settings.newTabPage) {
                    ForEach(ShellSettings.NewTabPage.allCases) { Text($0.title).tag($0) }
                }
                LabeledContent("Engine for new tabs") {
                    Button("Engine Settings…") { model.showSettings(.engine) }
                }
            }
        }
        .formStyle(.grouped)
    }
}
