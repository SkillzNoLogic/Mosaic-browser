import AppKit
import MosaicCore
import SwiftUI

/// Settings › Mosaics: choose, preview and customise the window's layout and look.
/// Owner: **shell**.
struct MosaicsSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.accessibilityReduceTransparency) private var systemReduceTransparency

    private var store: MosaicStore { model.mosaics }

    var body: some View {
        let layout = store.layout
        Form {
            Section {
                if let previewed = store.previewedPreset {
                    PreviewBanner(preset: previewed)
                }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 250), spacing: 16)], spacing: 16) {
                    ForEach(MosaicPresetID.allCases) { preset in
                        PresetCard(preset: preset)
                    }
                }
                .padding(.vertical, 6)
            } header: {
                Text("Presets")
            } footer: {
                SettingsNote("Switching is instant: pages keep running and nothing is reloaded. Each preset remembers your changes.")
            }

            Section {
                Picker("Tabs", selection: Binding(
                    get: { layout.tabPlacement },
                    set: { placement in store.replaceActiveLayout(store.layout.withTabPlacement(placement)) })
                ) {
                    ForEach(TabPlacement.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                if !layout.tabPlacement.isVertical {
                    Picker("Tab style", selection: bind(\.tabStyle)) {
                        ForEach(TabStyle.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                }
                Picker("Address bar", selection: bind(\.addressBar)) {
                    ForEach(AddressBarStyle.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                if layout.tabPlacement.isVertical {
                    Picker("Sidebar", selection: bind(\.sidebar)) {
                        ForEach(SidebarBehavior.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    Slider(value: bind(\.sidebarWidth), in: 180...420, step: 4) {
                        Text("Sidebar width")
                    }
                }
                Toggle("Bookmarks bar", isOn: bind(\.bookmarksBar))
            } header: {
                Text("Layout — \(layout.name)")
            }

            Section("Appearance") {
                Picker("Appearance", selection: bind(\.appearance.colorScheme)) {
                    ForEach(ColorSchemePreference.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                AccentPicker(accent: bind(\.appearance.accent))
                Picker("Density", selection: bind(\.appearance.density)) {
                    ForEach(ShellDensity.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                Slider(value: bind(\.appearance.cornerRadius), in: 0...16, step: 1) {
                    Text("Corner radius")
                } minimumValueLabel: { Text("Square").font(.caption) } maximumValueLabel: { Text("Round").font(.caption) }
                Slider(value: bind(\.appearance.contentInset), in: 0...16, step: 1) {
                    Text("Page inset")
                } minimumValueLabel: { Text("Flush").font(.caption) } maximumValueLabel: { Text("Floating").font(.caption) }
                Slider(value: bind(\.appearance.contentCornerRadius), in: 0...24, step: 1) {
                    Text("Page corners")
                }
                .disabled(layout.appearance.contentInset == 0 && layout.appearance.contentCornerRadius == 0)
                VStack(alignment: .leading, spacing: 4) {
                    Toggle("Liquid Glass", isOn: Binding(
                        get: { store.layout.appearance.material == .liquidGlass },
                        set: { on in store.update { $0.appearance.material = on ? .liquidGlass : .solid } }))
                    SettingsNote(glassNote)
                }
                Toggle("Glow around the page", isOn: bind(\.appearance.glow))
            }

            Section("Background") {
                BackgroundEditor(background: bind(\.appearance.background))
            }

            Section {
                HStack {
                    Button("Restore \(layout.name) Defaults") { store.restoreDefaults(store.activePreset) }
                        .disabled(!store.isCustomized(store.activePreset))
                    Spacer()
                }
            } footer: {
                SettingsNote("Restores the active preset's layout and appearance; other presets keep their changes.")
            }
        }
        .formStyle(.grouped)
    }

    private var glassNote: String {
        if #unavailable(macOS 26.0) {
            return "Liquid Glass needs macOS 26 or later; solid surfaces are used on this Mac."
        }
        let reduce = model.shellSettings.reduceTransparency.resolve(system: systemReduceTransparency)
        if reduce { return "Off while Reduce Transparency is on (Settings › Accessibility or System Settings)." }
        return "Glass toolbars and controls. Off: solid surfaces."
    }

    /// A binding to part of the active layout that saves on every change.
    private func bind<T>(_ keyPath: WritableKeyPath<MosaicLayout, T>) -> Binding<T> {
        Binding(
            get: { store.layout[keyPath: keyPath] },
            set: { value in store.update { $0[keyPath: keyPath] = value } })
    }
}

// MARK: - Preset cards

private struct PresetCard: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    let preset: MosaicPresetID

    var body: some View {
        let store = model.mosaics
        let layout = store.layout(for: preset)
        let isActive = store.activePreset == preset
        let isPreviewing = store.previewedPreset == preset
        VStack(alignment: .leading, spacing: 10) {
            MosaicPreview(layout: layout)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(isActive ? theme.accent : Color.primary.opacity(0.12), lineWidth: isActive ? 3 : 1))
                .onTapGesture { isActive ? store.endPreview() : store.preview(preset) }
            HStack(alignment: .firstTextBaseline) {
                Text(preset.title).font(.headline)
                if isActive {
                    Text("Active").font(.caption.weight(.semibold)).foregroundStyle(theme.accent)
                } else if store.isCustomized(preset) {
                    Text("Customised").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
            }
            Text(preset.summary)
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 8) {
                if isActive {
                    Button("Applied") {}.disabled(true)
                } else {
                    Button("Apply") { model.applyMosaic(preset) }
                        .buttonStyle(.borderedProminent)
                    Button(isPreviewing ? "End Preview" : "Preview") {
                        isPreviewing ? store.endPreview() : store.preview(preset)
                    }
                }
                Spacer()
                Menu {
                    if preset == .opera {
                        Toggle("GX Styling", isOn: Binding(
                            get: { layout.hasGXStyling },
                            set: { on in setGX(on) }))
                        Divider()
                    }
                    Button("Restore Defaults") { store.restoreDefaults(preset) }
                        .disabled(!store.isCustomized(preset))
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                .accessibilityLabel("\(preset.title) options")
            }
            .controlSize(.regular)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.primary.opacity(0.035)))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(preset.title) preset\(isActive ? ", active" : "")")
    }

    /// GX styling on Opera's layout (applying Opera first if needed, so the change is visible).
    private func setGX(_ on: Bool) {
        let store = model.mosaics
        if store.activePreset != .opera { store.apply(.opera) }
        store.update { layout in
            if on {
                layout = layout.withGXStyling()
            } else {
                let defaults = MosaicLayout.preset(.opera).appearance
                layout.appearance.colorScheme = defaults.colorScheme
                layout.appearance.accent = defaults.accent
                layout.appearance.glow = defaults.glow
                layout.appearance.background = defaults.background
            }
        }
    }
}

private struct PreviewBanner: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    let preset: MosaicPresetID

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "eye").foregroundStyle(theme.accent).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text("Previewing \(preset.title)").font(.headline)
                Text("The window around this page shows it now. Closing Settings without applying goes back.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Cancel") { model.mosaics.endPreview() }
            Button("Apply") { model.applyMosaic(preset) }.buttonStyle(.borderedProminent)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(theme.accent.opacity(0.1)))
    }
}

// MARK: - Colour controls

extension Color {
    /// `#RRGGBB(AA)` in sRGB, for storing a colour the user picked.
    var hexString: String? {
        guard let c = NSColor(self).usingColorSpace(.sRGB) else { return nil }
        return HexColor.string(red: c.redComponent, green: c.greenComponent, blue: c.blueComponent, alpha: c.alphaComponent)
    }
}

private struct AccentPicker: View {
    @Binding var accent: String

    private static let swatches = [
        HexColor.system, "#1A73E8", "#7C6CF2", "#E8458B", "#FF1B2D", "#FF8A00", "#22A559", "#0FA3B1", "#6E6E73",
    ]

    var body: some View {
        LabeledContent("Accent colour") {
            HStack(spacing: 7) {
                ForEach(Self.swatches, id: \.self) { hex in
                    Button {
                        accent = hex
                    } label: {
                        ZStack {
                            if hex == HexColor.system {
                                Circle().fill(AngularGradient(colors: [.red, .yellow, .green, .blue, .purple, .red], center: .center))
                            } else {
                                Circle().fill(Color(hex: hex))
                            }
                            if accent.caseInsensitiveCompare(hex) == .orderedSame {
                                Circle().strokeBorder(Color.primary, lineWidth: 2).padding(-3)
                            }
                        }
                        .frame(width: 20, height: 20)
                        .contentShape(Circle())
                    }
                    .buttonStyle(.plain)
                    .help(hex == HexColor.system ? "System accent colour" : hex)
                    .accessibilityLabel(hex == HexColor.system ? "System accent colour" : "Colour \(hex)")
                }
                ColorPicker("Custom accent colour", selection: Binding(
                    get: { Color(hex: accent) },
                    set: { if let hex = $0.hexString { accent = hex } }), supportsOpacity: false)
                    .labelsHidden()
            }
        }
    }
}

private struct BackgroundEditor: View {
    @Binding var background: BackgroundSpec

    private static let gradients: [(String, GradientSpec)] = [
        ("Lavender", GradientSpec(angle: 125, stops: [.init(color: "#C9BFF7", location: 0), .init(color: "#E7D6F5", location: 0.55), .init(color: "#BFD3F5", location: 1)])),
        ("Sunset", GradientSpec(angle: 135, stops: [.init(color: "#FFB88C", location: 0), .init(color: "#FF8FA3", location: 0.5), .init(color: "#C79BFF", location: 1)])),
        ("Ocean", GradientSpec(angle: 140, stops: [.init(color: "#A1C4FD", location: 0), .init(color: "#C2E9FB", location: 1)])),
        ("Mint", GradientSpec(angle: 120, stops: [.init(color: "#B8F1D3", location: 0), .init(color: "#D9F6C4", location: 1)])),
        ("Night", GradientSpec(angle: 160, stops: [.init(color: "#1B1024", location: 0), .init(color: "#0F0E15", location: 0.6), .init(color: "#24101A", location: 1)])),
        ("Graphite", GradientSpec(angle: 90, stops: [.init(color: "#3A3D44", location: 0), .init(color: "#24262B", location: 1)])),
    ]

    var body: some View {
        Picker("Fill", selection: $background.kind) {
            Text("System").tag(BackgroundSpec.Kind.system)
            Text("Colour").tag(BackgroundSpec.Kind.solid)
            Text("Gradient").tag(BackgroundSpec.Kind.gradient)
        }
        .pickerStyle(.segmented)
        switch background.kind {
        case .system:
            SettingsNote("The standard window colour, following light and dark mode.")
        case .solid:
            ColorPicker("Colour", selection: Binding(
                get: { Color(hex: background.color) },
                set: { if let hex = $0.hexString { background.color = hex } }), supportsOpacity: false)
        case .gradient:
            LabeledContent("Presets") {
                HStack(spacing: 8) {
                    ForEach(Self.gradients, id: \.0) { name, spec in
                        Button {
                            background.gradient = spec
                        } label: {
                            GradientFill(spec: spec)
                                .frame(width: 34, height: 22)
                                .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
                                .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous)
                                    .strokeBorder(background.gradient == spec ? Color.primary : Color.primary.opacity(0.15),
                                                  lineWidth: background.gradient == spec ? 2 : 1))
                        }
                        .buttonStyle(.plain)
                        .help(name)
                        .accessibilityLabel("\(name) gradient")
                    }
                }
            }
            ColorPicker("Start colour", selection: stopBinding(first: true), supportsOpacity: false)
            ColorPicker("End colour", selection: stopBinding(first: false), supportsOpacity: false)
            Slider(value: $background.gradient.angle, in: 0...360, step: 5) { Text("Angle") }
        }
    }

    private func stopBinding(first: Bool) -> Binding<Color> {
        Binding(
            get: {
                let stops = background.gradient.normalizedStops
                return Color(hex: (first ? stops.first : stops.last)?.color ?? "#808080")
            },
            set: { color in
                guard let hex = color.hexString else { return }
                var stops = background.gradient.normalizedStops
                if first { stops[0].color = hex } else { stops[stops.count - 1].color = hex }
                background.gradient.stops = stops
            })
    }
}
