import MosaicCore
import SwiftUI

/// Settings › Performance: sleeping tabs.
struct PerformanceSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        PerformanceSettingsForm(model: model, feature: model.tabsFeature)
    }
}

private struct PerformanceSettingsForm: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject var feature: TabsFeature

    var body: some View {
        Form {
            Section {
                Toggle("Unload inactive tabs", isOn: $feature.settings.sleep.unloadInactiveTabs)
                Picker("Unload a tab after", selection: $feature.settings.sleep.inactiveMinutes) {
                    ForEach(choices, id: \.self) { minutes in
                        Text(Self.label(minutes)).tag(minutes)
                    }
                }
                .disabled(!feature.settings.sleep.unloadInactiveTabs)
                Toggle("Never unload pinned tabs", isOn: $feature.settings.sleep.neverUnloadPinned)
                Toggle("Unload more tabs when memory is low", isOn: $feature.settings.sleep.respondToMemoryPressure)
                    .help("When macOS reports memory pressure, tabs are unloaded sooner; when it is critical, every background tab that may be is unloaded")
            } header: {
                Text("Sleeping Tabs")
            } footer: {
                Text("An unloaded tab keeps its place, title and address and is shown dimmed. Selecting it loads the page again. Never unloaded: the tab you are on, tabs playing audio, and pages that ask to stay open, such as a form with unsaved changes (Mozilla tabs).")
            }

            Section("Now") {
                LabeledContent("Unloaded tabs", value: "\(model.asleepTabCount) of \(model.tabs.count)")
                LabeledContent("Memory pressure", value: Self.label(feature.memoryPressure))
                if let checked = feature.lastSleepCheck {
                    LabeledContent("Last checked") { Text(checked, style: .relative) + Text(" ago") }
                }
                Button("Unload Background Tabs Now") { model.unloadBackgroundTabs() }
                    .help("Unloads every tab except the one you are on and the exceptions above")
            }

            Section {
                LabeledContent("Mozilla") {
                    Text("Keeps the tab’s back and forward history and scroll position.")
                        .multilineTextAlignment(.trailing)
                }
                LabeledContent("Chromium") {
                    Text("Reloads the page from its address; back and forward history is not kept.")
                        .multilineTextAlignment(.trailing)
                }
            } header: {
                Text("How Each Engine Unloads")
            } footer: {
                Text("Mosaic can’t yet tell when a Chromium tab is playing audio, so one playing in the background can be unloaded. Pin it and keep “Never unload pinned tabs” on to keep it playing.")
            }
        }
        .formStyle(.grouped)
    }

    private var choices: [Int] {
        let current = feature.settings.sleep.inactiveMinutes
        let base = SleepSettings.minuteChoices
        return base.contains(current) ? base : (base + [current]).sorted()
    }

    static func label(_ minutes: Int) -> String {
        if minutes < 60 { return "\(minutes) minutes" }
        let hours = minutes / 60
        return minutes % 60 == 0 ? (hours == 1 ? "1 hour" : "\(hours) hours") : "\(minutes) minutes"
    }

    static func label(_ pressure: MemoryPressure) -> String {
        switch pressure {
        case .normal: "Normal"
        case .warning: "High"
        case .critical: "Critical"
        }
    }
}
