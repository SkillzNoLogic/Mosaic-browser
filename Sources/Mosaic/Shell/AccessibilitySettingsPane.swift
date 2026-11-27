import AppKit
import SwiftUI

/// Settings › Accessibility: motion, transparency, text size, focus. Owner: **shell**.
struct AccessibilitySettingsPane: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @Environment(\.accessibilityReduceTransparency) private var systemReduceTransparency

    var body: some View {
        @Bindable var settings = model.shellSettings
        Form {
            Section("Motion and Transparency") {
                Picker("Reduce motion", selection: $settings.reduceMotion) {
                    ForEach(ShellSettings.Override.allCases) { Text($0.title).tag($0) }
                }
                SettingsNote("Turns off sliding and fading in Mosaic’s interface. System setting: \(systemReduceMotion ? "on" : "off").")
                Picker("Reduce transparency", selection: $settings.reduceTransparency) {
                    ForEach(ShellSettings.Override.allCases) { Text($0.title).tag($0) }
                }
                SettingsNote("Replaces Liquid Glass with solid surfaces. System setting: \(systemReduceTransparency ? "on" : "off").")
            }

            Section("Text and Controls") {
                Picker("Interface size", selection: $settings.textSize) {
                    ForEach(ShellSettings.TextSize.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                SettingsNote("Enlarges the toolbar, tabs, sidebar and their text. To enlarge web pages, use page zoom.")
                Toggle("Always show close buttons on tabs", isOn: $settings.alwaysShowTabCloseButtons)
            }

            Section("Keyboard") {
                Toggle("Prominent focus rings", isOn: $settings.prominentFocusRings)
                SettingsNote("Draws a thick ring in the accent colour around the focused button, field or tab.")
                LabeledContent("Move focus with Tab") {
                    Button("Keyboard Settings…") {
                        if let url = URL(string: "x-apple.systempreferences:com.apple.Keyboard-Settings.extension") {
                            NSWorkspace.shared.open(url)
                        }
                    }
                }
                SettingsNote("Turn on Keyboard navigation in System Settings › Keyboard to reach every control with Tab. ⌘L focuses the address field, ⌘T opens a tab, ⌘1–⌘9 select tabs, ⌘, opens Settings and Esc closes it.")
            }
        }
        .formStyle(.grouped)
    }
}
