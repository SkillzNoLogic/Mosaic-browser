import AppKit
import MosaicCore
import SwiftUI

/// The colour chart from Eminence: saturation/brightness square, hue bar, eyedropper, and
/// the colour in every common encoding (HEX, RGB, HSB, HSL, CMYK, LAB, OKLCH), all editable
/// and kept in sync. Edits `color`; `accessory` sits under the hue bar. Owner: **canvas**.
struct ColorChartEditor<Accessory: View>: View {
    var title: String
    @Binding var color: ColorValue
    @ViewBuilder var accessory: Accessory
    @AppStorage("canvas.colorEncoding", store: AppPaths.defaults) private var encoding = ColorEncoding.hex
    @Environment(\.shellTheme) private var theme

    /// Hue, saturation and brightness kept separately, so hue survives at grey and black.
    @State private var hue: Double = 0
    @State private var saturation: Double = 0
    @State private var brightness: Double = 0
    @State private var fields: [String] = []
    @State private var hexField = ""

    private var value: ColorValue { ColorValue(h: hue, s: saturation, brightness: brightness) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text(title).font(.headline)
                Spacer()
                Button { sampleScreen() } label: { Image(systemName: "eyedropper") }
                    .buttonStyle(.borderless)
                    .help("Pick a colour from the screen")
                    .accessibilityLabel("Pick a colour from the screen")
            }
            chart
            hueBar
            accessory
            encodingTabs
            editor
            Divider()
            allEncodings
        }
        .onAppear { load(color) }
        .onChange(of: encoding) { syncFields() }
        // A new colour from outside (another source selected in the studio).
        .onChange(of: color) { if color.hex != value.hex { load(color) } }
    }

    // MARK: Chart

    private var chart: some View {
        GeometryReader { geo in
            ZStack {
                ZStack {
                    Color(hue: hue / 360, saturation: 1, brightness: 1)
                    LinearGradient(colors: [.white, .white.opacity(0)], startPoint: .leading, endPoint: .trailing)
                    LinearGradient(colors: [.black.opacity(0), .black], startPoint: .top, endPoint: .bottom)
                }
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                // The handle stays whole at the corners, so it's drawn outside the clip.
                Circle()
                    .strokeBorder(.white, lineWidth: 2)
                    .shadow(radius: 1)
                    .frame(width: 16, height: 16)
                    .position(x: saturation / 100 * geo.size.width, y: (1 - brightness / 100) * geo.size.height)
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).onChanged { drag in
                saturation = min(max(drag.location.x / max(geo.size.width, 1), 0), 1) * 100
                brightness = (1 - min(max(drag.location.y / max(geo.size.height, 1), 0), 1)) * 100
                commit()
            })
        }
        .frame(height: 170)
        .accessibilityElement()
        .accessibilityLabel("Saturation and brightness")
        .accessibilityValue("Saturation \(Int(saturation)) percent, brightness \(Int(brightness)) percent")
        .accessibilityAdjustableAction { direction in
            brightness = min(max(brightness + (direction == .increment ? 5 : -5), 0), 100)
            commit()
        }
    }

    private var hueBar: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                LinearGradient(colors: stride(from: 0.0, through: 1.0, by: 1.0 / 6).map { Color(hue: $0, saturation: 1, brightness: 1) },
                               startPoint: .leading, endPoint: .trailing)
                    .clipShape(Capsule())
                Circle()
                    .fill(Color(hue: hue / 360, saturation: 1, brightness: 1))
                    .overlay { Circle().strokeBorder(.white, lineWidth: 2) }
                    .shadow(radius: 1)
                    .frame(width: 18, height: 18)
                    .offset(x: hue / 360 * (geo.size.width - 18))
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).onChanged { drag in
                hue = min(max(drag.location.x / max(geo.size.width, 1), 0), 1) * 360
                commit()
            })
        }
        .frame(height: 18)
        .accessibilityElement()
        .accessibilityLabel("Hue")
        .accessibilityValue("\(Int(hue)) degrees")
        .accessibilityAdjustableAction { direction in
            hue = (hue + (direction == .increment ? 10 : -10) + 360).truncatingRemainder(dividingBy: 360)
            commit()
        }
    }

    // MARK: Editing

    /// Seven equal tabs that always share the panel's width (a segmented Picker sizes to
    /// its labels and can overflow).
    private var encodingTabs: some View {
        HStack(spacing: 2) {
            ForEach(ColorEncoding.allCases) { item in
                Button { encoding = item } label: {
                    Text(item.rawValue)
                        .font(.caption.weight(.semibold))
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                        .frame(maxWidth: .infinity, minHeight: 24)
                        .foregroundStyle(encoding == item ? Color.white : .secondary)
                        .background {
                            if encoding == item { Capsule().fill(theme.accent) }
                        }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(item.rawValue)
                .accessibilityAddTraits(encoding == item ? .isSelected : [])
            }
        }
        .padding(3)
        .background(.quaternary.opacity(0.6), in: Capsule())
        .animation(theme.animation(.snappy(duration: 0.2)), value: encoding)
    }

    @ViewBuilder
    private var editor: some View {
        if encoding == .hex {
            TextField("Hex", text: $hexField, prompt: Text("#4A8CF2"))
                .labelsHidden()
                .textFieldStyle(.roundedBorder)
                .font(.system(.body, design: .monospaced))
                .onSubmit {
                    if let parsed = ColorValue(hex: hexField) { load(parsed); commit() } else { syncFields() }
                }
                .accessibilityLabel("Hex colour")
        } else {
            HStack(spacing: 8) {
                ForEach(Array(encoding.components.enumerated()), id: \.offset) { index, component in
                    VStack(spacing: 3) {
                        TextField(component.label, text: binding(index))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                            .multilineTextAlignment(.center)
                            .font(.system(.body, design: .monospaced))
                            .onSubmit { applyFields() }
                            .accessibilityLabel("\(encoding.rawValue) \(component.label)")
                        Text(component.label).font(.caption2).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    private func binding(_ index: Int) -> Binding<String> {
        Binding(get: { index < fields.count ? fields[index] : "" },
                set: { if index < fields.count { fields[index] = $0 } })
    }

    private var allEncodings: some View {
        VStack(alignment: .leading, spacing: 5) {
            ForEach(ColorEncoding.allCases) { item in
                let text = item.string(for: value)
                HStack {
                    Text(item.rawValue).font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
                    Text(text).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                    Spacer()
                    Button {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(text, forType: .string)
                    } label: { Image(systemName: "doc.on.doc").font(.caption) }
                        .buttonStyle(.borderless)
                        .help("Copy \(item.rawValue)")
                        .accessibilityLabel("Copy \(item.rawValue)")
                }
            }
        }
    }

    // MARK: State

    private func load(_ color: ColorValue) {
        let v = color.hsb
        // Keep the previous hue when the colour has none (greys).
        if v.s > 0 || v.b == 0 && hue == 0 { hue = v.h }
        saturation = v.s
        brightness = v.b
        syncFields()
    }

    /// Typed values for the current encoding, clamped to their ranges.
    private func applyFields() {
        load(encoding.color(parsing: fields, current: value))
        commit()
    }

    private func syncFields() {
        hexField = value.hex
        fields = zip(encoding.values(of: value), encoding.components).map { String(format: "%.\($1.digits)f", $0) }
    }

    private func commit() {
        if color.hex != value.hex { color = value }
        syncFields()
    }

    private func sampleScreen() {
        NSColorSampler().show { picked in
            guard let picked, let srgb = picked.usingColorSpace(.sRGB) else { return }
            let sampled = ColorValue(r: srgb.redComponent, g: srgb.greenComponent, b: srgb.blueComponent)
            Task { @MainActor in
                load(sampled)
                commit()
            }
        }
    }
}

extension ColorValue {
    var swiftUIColor: Color { Color(.sRGB, red: r, green: g, blue: b) }
}
