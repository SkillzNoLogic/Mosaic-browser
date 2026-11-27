import AppKit
import MosaicCore
import SwiftUI

/// Settings › Mosaics › Gradient Studio, ported from Eminence: a canvas the shape of the
/// window where you place as many colour sources as you like (they blend live as you drag
/// them), or a linear, radial or angular gradient with stops; each colour has the full colour
/// chart. Applies to the window, the New Tab page or the current workspace. While the target
/// already shows a gradient, edits apply live (a drag applies when it ends). Owner: **canvas**.
struct GradientStudioView: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @State private var gradient = GradientPresets.mesh[0].gradient
    /// A source ID (mesh) or "stop-<index>".
    @State private var selection: String?
    @State private var aspect = 16.0 / 10
    /// While a handle is being dragged, the window waits for the drop.
    @State private var dragging = false
    @State private var loaded = false
    @FocusState private var canvasFocused: Bool

    private var canvas: CanvasController { model.canvas }
    private var target: CanvasController.GradientTarget { canvas.gradientTarget }
    private var isInUse: Bool { canvas.usesGradient(target) }
    private var selectedSourceIndex: Int? {
        guard gradient.kind == .mesh else { return nil }
        return gradient.sources.firstIndex { $0.id == selection }
    }
    private var selectedStopIndex: Int? {
        guard gradient.kind != .mesh, let selection, selection.hasPrefix("stop-"),
              let index = Int(selection.dropFirst(5)), gradient.stops.indices.contains(index) else { return nil }
        return index
    }

    var body: some View {
        GeometryReader { geo in
            if geo.size.width >= 940 {
                HStack(spacing: 0) {
                    ScrollView { editorColumn.padding(20) }
                    Divider()
                    ScrollView { inspector.padding(20) }
                        .frame(width: min(380, geo.size.width * 0.36))
                }
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        editorColumn.padding(20)
                        Divider()
                        inspector.padding(20)
                    }
                }
            }
        }
        .onAppear(perform: load)
        .onChange(of: canvas.gradientTarget) { load() }
        .onChange(of: canvas.studioSelectionRequest) { _, request in
            guard let request else { return }
            if gradient.kind == .mesh, gradient.sources.indices.contains(request) {
                selection = gradient.sources[request].id
            } else if gradient.kind != .mesh, gradient.stops.indices.contains(request) {
                selection = "stop-\(request)"
            }
            canvas.studioSelectionRequest = nil
        }
        .onChange(of: gradient) { if isInUse && !dragging { applyLive() } }
        // Undo, a preset switch or a configuration changed the target's gradient.
        .onChange(of: canvas.background(for: target).gradient) { _, new in
            if isInUse, !dragging, new != gradient.clamped() { gradient = new }
        }
    }

    private func load() {
        aspect = Self.mainWindowAspect()
        let current = canvas.background(for: target)
        if canvas.usesGradient(target) || current.kind == .gradient {
            gradient = current.gradient
        } else if !loaded {
            gradient = GradientPresets.mesh[0].gradient
        }
        selection = nil
        loaded = true
    }

    private func applyLive() {
        canvas.useGradient(gradient, for: target, coalesce: "gradient")
    }

    // MARK: Editor column

    private var editorColumn: some View {
        VStack(alignment: .leading, spacing: 14) {
            toolbar
            studioCanvas
            if gradient.kind != .mesh { stopBar }
            presetStrip
        }
    }

    private var toolbar: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Gradient Studio").font(.title2.weight(.semibold))
                    Text(gradient.kind == .mesh
                         ? "Double-click to add a colour · drag to move · Delete removes"
                         : "Click the bar to add a stop · drag stops and handles · Delete removes")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Picker("Apply to", selection: Binding(get: { canvas.gradientTarget }, set: { canvas.gradientTarget = $0 })) {
                    Text("Window").tag(CanvasController.GradientTarget.window)
                    Text("New Tab Page").tag(CanvasController.GradientTarget.newTab)
                    Text("Workspace “\(canvas.workspaceName)”").tag(CanvasController.GradientTarget.workspace)
                }
                .fixedSize()
                useButton
            }
            HStack(spacing: 14) {
                Picker("Kind", selection: Binding(get: { gradient.kind }, set: { changeKind($0) })) {
                    Text("Free-form").tag(GradientSpec.Kind.mesh)
                    Text("Linear").tag(GradientSpec.Kind.linear)
                    Text("Radial").tag(GradientSpec.Kind.radial)
                    Text("Angular").tag(GradientSpec.Kind.angular)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .accessibilityLabel("Gradient kind")
                Spacer(minLength: 0)
                Toggle(isOn: $gradient.raw) { Text("Raw").fixedSize() }
                    .toggleStyle(.switch)
                    .help("Show the gradient exactly as it is in light and dark mode, instead of dimming or lifting it to keep the toolbar legible")
            }
            HStack(spacing: 14) {
                if gradient.kind == .mesh {
                    Button { addSource(at: CGPoint(x: 0.5, y: 0.5)) } label: { Label("Add Colour", systemImage: "plus.circle") }
                        .disabled(gradient.sources.count >= GradientSpec.maxSources)
                    Button(role: .destructive) { removeSelected() } label: { Label("Remove", systemImage: "minus.circle") }
                        .disabled(selectedSourceIndex == nil || gradient.sources.count <= 1)
                    Divider().frame(height: 18)
                    HStack(spacing: 6) {
                        Text("Blend").foregroundStyle(.secondary).fixedSize()
                        Image(systemName: "circle.dashed").foregroundStyle(.secondary).help("Soft")
                        Slider(value: $gradient.sharpness, in: GradientSpec.sharpnessRange).frame(minWidth: 70, maxWidth: 150)
                            .accessibilityLabel("Blend")
                            .accessibilityValue(gradient.sharpness < 2 ? "Soft" : gradient.sharpness > 4 ? "Defined" : "Medium")
                        Image(systemName: "circle").foregroundStyle(.secondary).help("Defined")
                    }
                    .help("How sharply the colours meet")
                } else {
                    Button { addStop(at: 0.5) } label: { Label("Add Stop", systemImage: "plus.circle") }
                        .disabled(gradient.stops.count >= GradientSpec.maxStops)
                    Button(role: .destructive) { removeSelected() } label: { Label("Remove", systemImage: "minus.circle") }
                        .disabled(selectedStopIndex == nil || gradient.normalizedStops.count <= 2)
                    if gradient.kind != .radial {
                        Divider().frame(height: 18)
                        HStack(spacing: 6) {
                            Text("Angle").foregroundStyle(.secondary).fixedSize()
                            Slider(value: $gradient.angle, in: 0...360).frame(minWidth: 70, maxWidth: 150)
                                .accessibilityLabel("Angle")
                            Text("\(Int(gradient.angle.rounded()))°").font(.callout.monospacedDigit()).frame(width: 38, alignment: .trailing).fixedSize()
                        }
                    }
                }
                Spacer(minLength: 0)
            }
            .labelStyle(.titleAndIcon)
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    @ViewBuilder private var useButton: some View {
        let same = isInUse && canvas.background(for: target).gradient == gradient.clamped()
        let label = Label(isInUse ? "In Use" : "Use as Background", systemImage: isInUse ? "checkmark" : "paintbrush")
        if #available(macOS 26.0, *), theme.glassEnabled {
            Button { canvas.useGradient(gradient, for: target) } label: { label }
                .buttonStyle(.glassProminent)
                .disabled(same)
        } else {
            Button { canvas.useGradient(gradient, for: target) } label: { label }
                .buttonStyle(.borderedProminent)
                .disabled(same)
        }
    }

    // MARK: Canvas

    private var studioCanvas: some View {
        GeometryReader { geo in
            let size = geo.size
            ZStack {
                CanvasGradientBackground(spec: gradient)
                    .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(.white.opacity(0.18), lineWidth: 1)
                    }
                    .onTapGesture(count: 2) { location in
                        guard gradient.kind == .mesh else { return }
                        addSource(at: CGPoint(x: location.x / max(size.width, 1), y: location.y / max(size.height, 1)))
                    }
                    .onTapGesture { selection = nil; canvasFocused = true }
                    .accessibilityHidden(true)
                if gradient.kind == .mesh {
                    ForEach(Array(gradient.sources.enumerated()), id: \.element.id) { index, source in
                        SourceHandle(color: source.colorValue.swiftUIColor, isSelected: selection == source.id)
                            .position(x: source.x * size.width, y: source.y * size.height)
                            .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("studio"))
                                .onChanged { drag in
                                    dragging = true
                                    moveSource(source.id, to: drag.location, in: size)
                                }
                                .onEnded { _ in
                                    dragging = false
                                    if isInUse { applyLive() }
                                })
                            .accessibilityElement()
                            .accessibilityLabel("Colour \(index + 1), \(source.color)")
                            .accessibilityValue("\(Int(source.x * 100)) percent across, \(Int(source.y * 100)) percent down")
                            .accessibilityAddTraits(.isButton)
                            .accessibilityAction { selection = source.id }
                    }
                } else {
                    directionHandles(size: size)
                }
            }
            .coordinateSpace(.named("studio"))
        }
        .aspectRatio(aspect, contentMode: .fit)
        .frame(maxWidth: .infinity, maxHeight: 360)
        .focusable()
        .focusEffectDisabled()
        .focused($canvasFocused)
        .onKeyPress(keys: [.delete, .deleteForward]) { _ in
            removeSelected()
            return .handled
        }
        .onKeyPress(keys: [.leftArrow, .rightArrow, .upArrow, .downArrow]) { press in
            nudge(press.key, by: press.modifiers.contains(.shift) ? 0.1 : 0.02) ? .handled : .ignored
        }
        .animation(theme.animation(.snappy(duration: 0.2)), value: gradient.sources.count)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Gradient canvas")
        .accessibilityHint(gradient.kind == .mesh ? "Select a colour, then use the arrow keys to move it." : "")
    }

    /// Linear/angular: a knob on a line through the centre sets the angle; radial/angular: the centre moves.
    @ViewBuilder private func directionHandles(size: CGSize) -> some View {
        let center = CGPoint(x: gradient.centerX * size.width, y: gradient.centerY * size.height)
        let origin = gradient.kind == .linear ? CGPoint(x: size.width / 2, y: size.height / 2) : center
        let radians = gradient.angle * .pi / 180
        let reach = min(size.width, size.height) * 0.36
        let knob = CGPoint(x: origin.x + cos(radians) * reach, y: origin.y + sin(radians) * reach)
        if gradient.kind != .radial {
            Path { path in
                path.move(to: CGPoint(x: origin.x - cos(radians) * reach, y: origin.y - sin(radians) * reach))
                path.addLine(to: knob)
            }
            .stroke(.white.opacity(0.85), style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
            .shadow(color: .black.opacity(0.3), radius: 2)
            .allowsHitTesting(false)
            Circle()
                .fill(.white)
                .frame(width: 18, height: 18)
                .shadow(color: .black.opacity(0.35), radius: 3, y: 1)
                .position(knob)
                .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("studio")).onChanged { drag in
                    dragging = true
                    var degrees = atan2(drag.location.y - origin.y, drag.location.x - origin.x) * 180 / .pi
                    if degrees < 0 { degrees += 360 }
                    gradient.angle = degrees.rounded()
                }.onEnded { _ in
                    dragging = false
                    if isInUse { applyLive() }
                })
                .help("Drag to set the angle")
                .accessibilityLabel("Angle, \(Int(gradient.angle)) degrees")
        }
        if gradient.kind != .linear {
            SourceHandle(color: .white.opacity(0.3), isSelected: false)
                .position(center)
                .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("studio")).onChanged { drag in
                    dragging = true
                    gradient.centerX = min(max(drag.location.x / max(size.width, 1), 0), 1)
                    gradient.centerY = min(max(drag.location.y / max(size.height, 1), 0), 1)
                }.onEnded { _ in
                    dragging = false
                    if isInUse { applyLive() }
                })
                .help("Drag to move the centre")
                .accessibilityLabel("Centre")
        }
    }

    // MARK: Stop bar

    private var stopBar: some View {
        GeometryReader { geo in
            let width = max(geo.size.width - 16, 1)
            ZStack(alignment: .leading) {
                LinearGradient(gradient: Gradient(stops: gradient.normalizedStops.map {
                    Gradient.Stop(color: Color(hex: $0.color), location: $0.location)
                }), startPoint: .leading, endPoint: .trailing)
                    .frame(height: 22)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(Color.primary.opacity(0.15)))
                    .padding(.horizontal, 8)
                    .onTapGesture { location in addStop(at: Double((location.x - 8) / width)) }
                    .accessibilityHidden(true)
                ForEach(Array(gradient.stops.enumerated()), id: \.offset) { index, stop in
                    let id = "stop-\(index)"
                    RoundedRectangle(cornerRadius: 4, style: .continuous)
                        .fill(Color(hex: stop.color))
                        .frame(width: 16, height: 30)
                        .overlay(RoundedRectangle(cornerRadius: 4, style: .continuous)
                            .strokeBorder(.white, lineWidth: selection == id ? 3 : 2))
                        .shadow(color: .black.opacity(0.35), radius: 2, y: 1)
                        .position(x: 8 + min(max(stop.location, 0), 1) * width, y: geo.size.height / 2)
                        .gesture(DragGesture(minimumDistance: 0).onChanged { drag in
                            dragging = true
                            selection = id
                            gradient.stops[index].location = min(max(Double((drag.location.x - 8) / width), 0), 1)
                        }.onEnded { _ in
                            dragging = false
                            if isInUse { applyLive() }
                        })
                        .accessibilityElement()
                        .accessibilityLabel("Stop \(index + 1), \(stop.color)")
                        .accessibilityValue("\(Int(stop.location * 100)) percent")
                        .accessibilityAddTraits(.isButton)
                        .accessibilityAction { selection = id }
                        .accessibilityAdjustableAction { direction in
                            let step = direction == .increment ? 0.05 : -0.05
                            gradient.stops[index].location = min(max(gradient.stops[index].location + step, 0), 1)
                        }
                }
            }
        }
        .frame(height: 34)
    }

    // MARK: Presets

    private var presetStrip: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Presets").font(.headline)
            ScrollView(.horizontal) {
                HStack(spacing: 12) {
                    ForEach(GradientPresets.all, id: \.name) { preset in
                        Button {
                            withAnimation(theme.animation(.smooth(duration: 0.3))) {
                                var next = preset.gradient
                                // Keep the finish the user chose.
                                next.noise = gradient.noise
                                next.opacity = gradient.opacity
                                next.raw = gradient.raw
                                gradient = next
                                selection = nil
                            }
                        } label: {
                            VStack(spacing: 5) {
                                PresetThumbnail(gradient: preset.gradient)
                                    .equatable()
                                    .frame(width: 96, height: 96 / max(aspect, 0.5))
                                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                                    .overlay {
                                        let chosen = sameDesign(preset.gradient)
                                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                                            .strokeBorder(chosen ? theme.accent : .white.opacity(0.15), lineWidth: chosen ? 2.5 : 1)
                                    }
                                Text(preset.name).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .buttonStyle(.plain)
                        .help("\(preset.name): \(preset.gradient.colors.count) colours")
                        .accessibilityLabel("\(preset.name) preset")
                    }
                }
                .padding(.bottom, 6)
            }
            .scrollIndicators(.never)
        }
    }

    private func sameDesign(_ preset: GradientSpec) -> Bool {
        var a = gradient, b = preset
        a.noise = 0; a.opacity = 1; a.raw = false
        b.noise = 0; b.opacity = 1; b.raw = false
        return a == b
    }

    // MARK: Inspector

    @ViewBuilder
    private var inspector: some View {
        VStack(alignment: .leading, spacing: 18) {
            if let index = selectedSourceIndex {
                ColorChartEditor(title: "Colour \(index + 1) of \(gradient.sources.count)",
                                 color: sourceColorBinding(gradient.sources[index].id)) {
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text("Reach").font(.callout)
                            Spacer()
                            Text(String(format: "%.1f×", gradient.sources[index].spread))
                                .font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                        }
                        Slider(value: spreadBinding(gradient.sources[index].id), in: GradientSpec.spreadRange)
                            .accessibilityLabel("Reach")
                    }
                }
            } else if let index = selectedStopIndex {
                ColorChartEditor(title: "Stop \(index + 1) of \(gradient.stops.count)", color: stopColorBinding(index)) {
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text("Position").font(.callout)
                            Spacer()
                            Text("\(Int((gradient.stops[index].location * 100).rounded()))%")
                                .font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                        }
                        Slider(value: $gradient.stops[index].location, in: 0...1).accessibilityLabel("Position")
                    }
                }
            } else {
                colourList
            }
            Divider()
            finish
            targetOptions
        }
    }

    private var colourList: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(gradient.kind == .mesh ? "Pick a colour source" : "Pick a stop", systemImage: "hand.tap").font(.headline)
            Text(gradient.kind == .mesh
                 ? "Click a circle on the canvas to change its colour in any encoding (HEX, RGB, HSB, HSL, CMYK, LAB or OKLCH) and how far it reaches. Double-click empty space to add another, up to \(GradientSpec.maxSources). Colours fade into each other and update as you drag."
                 : "Click a stop under the canvas to change its colour in any encoding and where it sits. Click the bar to add a stop where you clicked.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            let colors: [(id: String, hex: String)] = gradient.kind == .mesh
                ? gradient.sources.map { ($0.id, $0.color) }
                : gradient.stops.enumerated().map { ("stop-\($0.offset)", $0.element.color) }
            Text("\(colors.count) colours").font(.callout.weight(.semibold))
            ForEach(Array(colors.enumerated()), id: \.element.id) { index, item in
                Button { selection = item.id } label: {
                    HStack(spacing: 10) {
                        Circle().fill(Color(hex: item.hex)).frame(width: 18, height: 18)
                            .overlay { Circle().strokeBorder(.white.opacity(0.5), lineWidth: 1) }
                        Text(gradient.kind == .mesh ? "Colour \(index + 1)" : "Stop \(index + 1)")
                        Spacer()
                        Text(item.hex).font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }

    /// Grain, strength and Raw: how the gradient sits in the window.
    private var finish: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Finish").font(.headline)
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text("Grain")
                    Spacer()
                    Text("\(Int((gradient.noise * 100).rounded()))%").monospacedDigit().foregroundStyle(.secondary)
                }
                Slider(value: $gradient.noise, in: 0...1).accessibilityLabel("Grain")
            }
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text("Strength")
                    Spacer()
                    Text("\(Int((gradient.opacity * 100).rounded()))%").monospacedDigit().foregroundStyle(.secondary)
                }
                Slider(value: $gradient.opacity, in: GradientSpec.opacityRange).accessibilityLabel("Strength")
                Text("Lower values let the system window colour, which follows light and dark mode, show through.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Text(gradient.raw
                 ? "Raw: shown exactly as chosen in light and dark mode."
                 : "Light colours are dimmed in dark mode and dark ones lifted in light mode, so toolbar text stays legible. Turn on Raw to keep them exact.")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if model.shellSettings.reduceTransparency != .off {
                Text("The background is always drawn opaque, so it stays readable with Reduce Transparency; toolbars then use solid surfaces.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    @ViewBuilder private var targetOptions: some View {
        switch target {
        case .window:
            EmptyView()
        case .newTab:
            VStack(alignment: .leading, spacing: 8) {
                Text("New Tab Page").font(.headline)
                Picker("Background", selection: Binding(
                    get: { canvas.layout.appearance.newTabBackground.mode },
                    set: { canvas.set(\.appearance.newTabBackground.mode, $0) })) {
                    ForEach(NewTabBackground.Mode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                Text("“Use as Background” gives the New Tab page its own gradient.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        case .workspace:
            VStack(alignment: .leading, spacing: 8) {
                Text("Workspace “\(canvas.workspaceName)”").font(.headline)
                Text(canvas.layout.appearance.workspaceBackgrounds[canvas.workspaceID] == nil
                     ? "Uses the window’s background. “Use as Background” gives it its own; switching workspaces then switches backgrounds."
                     : "Has its own background; other workspaces keep the window’s.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Button("Use the Window’s Background") { canvas.clearWorkspaceBackground() }
                    .disabled(canvas.layout.appearance.workspaceBackgrounds[canvas.workspaceID] == nil)
            }
        }
    }

    // MARK: Bindings

    private func sourceColorBinding(_ id: String) -> Binding<ColorValue> {
        Binding(get: { gradient.sources.first { $0.id == id }?.colorValue ?? .fallback },
                set: { value in
                    guard let i = gradient.sources.firstIndex(where: { $0.id == id }),
                          gradient.sources[i].color.uppercased() != value.hex else { return }
                    gradient.sources[i].color = value.hex
                })
    }

    private func spreadBinding(_ id: String) -> Binding<Double> {
        Binding(get: { gradient.sources.first { $0.id == id }?.spread ?? 1 },
                set: { value in
                    if let i = gradient.sources.firstIndex(where: { $0.id == id }) { gradient.sources[i].spread = value }
                })
    }

    private func stopColorBinding(_ index: Int) -> Binding<ColorValue> {
        Binding(get: { gradient.stops.indices.contains(index) ? ColorValue(hex: gradient.stops[index].color) ?? .fallback : .fallback },
                set: { value in
                    guard gradient.stops.indices.contains(index), gradient.stops[index].color.uppercased() != value.hex else { return }
                    gradient.stops[index].color = value.hex
                })
    }

    // MARK: Editing

    private func changeKind(_ kind: GradientSpec.Kind) {
        var next = gradient
        if next.kind != .mesh, next.stops.count < 2 { next.stops = next.normalizedStops }
        next = next.converted(to: kind)
        if kind != .mesh, next.stops.count < 2 { next.stops = next.normalizedStops }
        gradient = next
        selection = nil
    }

    private func moveSource(_ id: String, to location: CGPoint, in size: CGSize) {
        guard let i = gradient.sources.firstIndex(where: { $0.id == id }), size.width > 0, size.height > 0 else { return }
        if selection != id { selection = id }
        gradient.sources[i].x = min(max(location.x / size.width, 0), 1)
        gradient.sources[i].y = min(max(location.y / size.height, 0), 1)
    }

    /// Adds a source showing the colour already at that point, so nothing jumps, and
    /// selects it for editing.
    private func addSource(at point: CGPoint) {
        guard gradient.sources.count < GradientSpec.maxSources else { return }
        if gradient.sources.isEmpty { gradient.sources = gradient.meshSources }
        let colour = gradient.meshColor(atX: point.x, y: point.y, aspect: aspect)
        let source = GradientSource(x: min(max(point.x, 0), 1), y: min(max(point.y, 0), 1), color: colour.hex)
        gradient.sources.append(source)
        selection = source.id
        canvasFocused = true
    }

    private func addStop(at location: Double) {
        guard gradient.stops.count < GradientSpec.maxStops else { return }
        let t = min(max(location, 0), 1)
        if gradient.stops.count < 2 { gradient.stops = gradient.normalizedStops }
        gradient.stops.append(GradientStop(color: gradient.stopColor(at: t).hex, location: t))
        selection = "stop-\(gradient.stops.count - 1)"
    }

    private func removeSelected() {
        if let index = selectedSourceIndex, gradient.sources.count > 1 {
            gradient.sources.remove(at: index)
            selection = nil
        } else if let index = selectedStopIndex, gradient.normalizedStops.count > 2 {
            gradient.stops.remove(at: index)
            selection = nil
        }
    }

    /// Arrow keys move the selected source (or stop) by `step` of the canvas.
    private func nudge(_ key: KeyEquivalent, by step: Double) -> Bool {
        if let i = selectedSourceIndex {
            switch key {
            case .leftArrow: gradient.sources[i].x = max(gradient.sources[i].x - step, 0)
            case .rightArrow: gradient.sources[i].x = min(gradient.sources[i].x + step, 1)
            case .upArrow: gradient.sources[i].y = max(gradient.sources[i].y - step, 0)
            case .downArrow: gradient.sources[i].y = min(gradient.sources[i].y + step, 1)
            default: return false
            }
            return true
        }
        if let i = selectedStopIndex, key == .leftArrow || key == .rightArrow {
            gradient.stops[i].location = min(max(gradient.stops[i].location + (key == .leftArrow ? -step : step), 0), 1)
            return true
        }
        return false
    }

    /// The main window's width ÷ height, so the canvas is the shape of Mosaic.
    static func mainWindowAspect() -> Double {
        let window = NSApp.mainWindow ?? NSApp.windows.first { $0.isVisible && $0.frame.width > 400 }
        guard let frame = window?.frame, frame.height > 0 else { return 16.0 / 10 }
        return min(max(frame.width / frame.height, 0.6), 3)
    }
}

/// A colour source on the canvas: its colour in a white ring, larger when selected.
private struct SourceHandle: View {
    @Environment(\.shellTheme) private var theme
    let color: Color
    let isSelected: Bool

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 28, height: 28)
            .overlay { Circle().strokeBorder(.white, lineWidth: isSelected ? 4 : 2.5) }
            .shadow(color: .black.opacity(0.35), radius: 4, y: 2)
            .scaleEffect(isSelected ? 1.2 : 1)
            .animation(theme.animation(.snappy(duration: 0.2)), value: isSelected)
            .contentShape(Circle().inset(by: -8))
    }
}

/// A preset's preview. Equatable, so dragging a source (which redraws the studio) doesn't
/// re-render all the thumbnails.
private struct PresetThumbnail: View, Equatable {
    let gradient: GradientSpec

    var body: some View {
        if gradient.kind == .mesh {
            MeshGradientImage(spec: gradient, resolution: 64)
        } else {
            CanvasGradientFill(spec: gradient)
        }
    }
}
