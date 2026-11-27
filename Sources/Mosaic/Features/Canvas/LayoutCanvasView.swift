import AppKit
import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// Settings › Mosaics › Customise Layout: a large live miniature of the window, built from
/// the active layout, whose buttons can be dragged between rows and reordered, a palette of
/// what isn't placed, and keyboard/menu alternatives for every drag. Changes apply to the
/// real window as they are made. Owner: **canvas**.
struct LayoutCanvasEditor: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @FocusState private var canvasFocused: Bool
    @State private var showsSave = false

    private var canvas: CanvasController { model.canvas }

    var body: some View {
        let layout = canvas.layout
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header(layout)
                LayoutOptionsBar()
                WindowMiniature(layout: layout, focused: canvasFocused)
                    .focusable()
                    .focusEffectDisabled()
                    .focused($canvasFocused)
                    .onKeyPress(phases: .down, action: handleKey)
                    .overlay {
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .strokeBorder(theme.accent, lineWidth: canvasFocused ? 2.5 : 0)
                            .allowsHitTesting(false)
                    }
                    .accessibilityElement(children: .contain)
                    .accessibilityLabel("Window layout")
                    .accessibilityHint("Select an item with the arrow keys; Option-arrow keys move it.")
                StatusLine()
                SelectionInspector()
                CanvasPalette()
            }
            .padding(20)
            .frame(maxWidth: 1100, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .onDisappear { canvas.endGrab(commit: true) }
    }

    private func header(_ layout: MosaicLayout) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text("Customise Layout").font(.title2.weight(.semibold))
                Text("Drag buttons between rows and into Available Items. Changes apply to this window as you make them.")
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 12)
            ControlGroup {
                Button { canvas.undo() } label: { Label("Undo", systemImage: "arrow.uturn.backward") }
                    .disabled(!canvas.canUndo)
                    .help("Undo (⌘Z while the layout is focused)")
                Button { canvas.redo() } label: { Label("Redo", systemImage: "arrow.uturn.forward") }
                    .disabled(!canvas.canRedo)
                    .help("Redo (⇧⌘Z while the layout is focused)")
            }
            .fixedSize()
            Button { canvas.resetToPreset() } label: {
                Label("Reset to \(model.mosaics.activePreset.title)", systemImage: "arrow.counterclockwise")
            }
            .disabled(layout == MosaicLayout.preset(model.mosaics.activePreset))
            .help("Return this preset to its factory layout and look (undoable)")
            Button { showsSave = true } label: { Label("Save…", systemImage: "square.and.arrow.down") }
                .help("Save this layout and its appearance as a named configuration")
                .popover(isPresented: $showsSave, arrowEdge: .bottom) {
                    SaveConfigurationPopover(isPresented: $showsSave)
                }
        }
        .labelStyle(.titleAndIcon)
    }

    private func handleKey(_ press: KeyPress) -> KeyPress.Result {
        let option = press.modifiers.contains(.option)
        let moving = option || canvas.grab != nil
        switch press.key {
        case .leftArrow: moving ? canvas.moveSelected(.left) : canvas.moveSelection(.left)
        case .rightArrow: moving ? canvas.moveSelected(.right) : canvas.moveSelection(.right)
        case .upArrow: moving ? canvas.moveSelected(.up) : canvas.moveSelection(.up)
        case .downArrow: moving ? canvas.moveSelected(.down) : canvas.moveSelection(.down)
        case .return, .space: canvas.toggleGrab()
        case .escape:
            guard canvas.grab != nil else { return .ignored }
            canvas.endGrab(commit: false)
        case .delete, .deleteForward: canvas.removeSelected()
        default:
            guard press.modifiers.contains(.command), press.characters.lowercased() == "z" else { return .ignored }
            press.modifiers.contains(.shift) ? canvas.redo() : canvas.undo()
        }
        return .handled
    }
}

// MARK: - Options

/// Tab placement, sidebar and the other layout choices, next to the canvas.
private struct LayoutOptionsBar: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        let canvas = model.canvas
        let layout = canvas.layout
        FlowLayout(spacing: 18, lineSpacing: 10) {
            labelled("Tabs") {
                Picker("Tabs", selection: Binding(get: { layout.tabPlacement }, set: { canvas.setTabPlacement($0) })) {
                    Text("Top").tag(TabPlacement.top)
                    Text("Left").tag(TabPlacement.sidebarLeading)
                    Text("Right").tag(TabPlacement.sidebarTrailing)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
            }
            if layout.tabPlacement.isVertical {
                labelled("Sidebar") {
                    Picker("Sidebar", selection: Binding(get: { layout.sidebar }, set: { canvas.set(\.sidebar, $0) })) {
                        ForEach(SidebarBehavior.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    .labelsHidden().fixedSize()
                }
                labelled("Width") {
                    Slider(value: Binding(get: { layout.sidebarWidth }, set: { canvas.set(\.sidebarWidth, $0, coalesce: "sidebarWidth") }),
                           in: 180...420)
                        .frame(width: 120)
                        .accessibilityLabel("Sidebar width")
                }
            } else {
                labelled("Tab style") {
                    Picker("Tab style", selection: Binding(get: { layout.tabStyle }, set: { canvas.set(\.tabStyle, $0) })) {
                        ForEach(TabStyle.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    .labelsHidden().fixedSize()
                }
            }
            labelled("Address bar") {
                Picker("Address bar", selection: Binding(get: { layout.addressBar }, set: { canvas.set(\.addressBar, $0) })) {
                    ForEach(AddressBarStyle.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                .labelsHidden().fixedSize()
            }
            Toggle("Bookmarks bar", isOn: Binding(get: { layout.bookmarksBar }, set: { canvas.set(\.bookmarksBar, $0) }))
                .toggleStyle(.checkbox)
                .fixedSize()
        }
        .controlSize(.regular)
    }

    private func labelled<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        HStack(spacing: 6) {
            Text(title).foregroundStyle(.secondary)
            content()
        }
        .fixedSize()
    }
}

// MARK: - Miniature

/// The window drawn from the layout: each region a drop zone, each component a chip.
private struct WindowMiniature: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.colorScheme) private var systemScheme
    let layout: MosaicLayout
    let focused: Bool

    var body: some View {
        let vertical = layout.tabPlacement.isVertical
        let sidebarShown = vertical || layout.contains(.sidebarToggle)
        VStack(spacing: 0) {
            RegionZone(region: .titleBar, axis: .horizontal, leadingInset: 64)
                .frame(minHeight: 46)
            HStack(spacing: 0) {
                RegionZone(region: .rail, axis: .vertical)
                    .frame(width: 58)
                if layout.tabPlacement != .sidebarTrailing {
                    SidebarZone(layout: layout, shown: sidebarShown)
                }
                VStack(spacing: 0) {
                    RegionZone(region: .toolbar, axis: .horizontal)
                        .frame(minHeight: 46)
                        .background { if layout.appearance.toolbarSurface { ShellColors.surface.opacity(0.9) } }
                    RegionZone(region: .tabBar, axis: .horizontal)
                        .frame(minHeight: 40)
                    if layout.bookmarksBar {
                        BookmarksStrip()
                    }
                    PagePlaceholder(layout: layout)
                }
                if layout.tabPlacement == .sidebarTrailing {
                    SidebarZone(layout: layout, shown: true)
                }
            }
        }
        .background {
            BackgroundFill(spec: layout.appearance.background(forWorkspace: model.selectedWorkspaceID.uuidString))
        }
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Color.primary.opacity(0.15)))
        .overlay(alignment: .topLeading) {
            HStack(spacing: 7) {
                Circle().fill(Color(red: 1, green: 0.37, blue: 0.34))
                Circle().fill(Color(red: 1, green: 0.74, blue: 0.18))
                Circle().fill(Color(red: 0.16, green: 0.79, blue: 0.25))
            }
            .frame(width: 50, height: 12)
            .padding(.leading, 14)
            .padding(.top, 17)
            .accessibilityHidden(true)
        }
        .frame(minHeight: 430)
        .shadow(color: .black.opacity(0.12), radius: 10, y: 3)
        .environment(\.colorScheme, scheme)
    }

    private var scheme: ColorScheme {
        switch layout.appearance.colorScheme {
        case .system: systemScheme
        case .light: .light
        case .dark: .dark
        }
    }
}

private struct SidebarZone: View {
    let layout: MosaicLayout
    let shown: Bool

    var body: some View {
        let vertical = layout.tabPlacement.isVertical
        VStack(alignment: .leading, spacing: 6) {
            RegionZone(region: .sidebarTop, axis: .horizontal).frame(minHeight: 40)
            RegionZone(region: .sidebarMiddle, axis: .horizontal).frame(minHeight: 40)
            VStack(alignment: .leading, spacing: 5) {
                if vertical {
                    ForEach(0..<6, id: \.self) { index in
                        HStack(spacing: 6) {
                            RoundedRectangle(cornerRadius: 3).fill(Color.primary.opacity(index == 0 ? 0.35 : 0.18))
                                .frame(width: 12, height: 12)
                            Capsule().fill(Color.primary.opacity(0.16)).frame(width: CGFloat(90 - index * 9), height: 5)
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 8)
                        .frame(height: 24)
                        .background(RoundedRectangle(cornerRadius: 6).fill(index == 0 ? ShellColors.sidebarSelection : .clear))
                    }
                    Text("Tabs").font(.caption2).foregroundStyle(.tertiary).padding(.leading, 8)
                } else {
                    Text(shown ? "Sidebar — hidden until the Sidebar button opens it"
                               : "Sidebar — add the Sidebar button to use it")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(8)
                }
                Spacer(minLength: 0)
            }
            .frame(maxHeight: .infinity, alignment: .top)
            .accessibilityHidden(true)
            RegionZone(region: .sidebarBottom, axis: .horizontal).frame(minHeight: 40)
        }
        .padding(6)
        .frame(width: vertical ? 236 : 190)
        .background(Color.primary.opacity(vertical ? 0.03 : 0.06))
        .opacity(vertical || shown ? 1 : 0.8)
        .overlay(alignment: layout.tabPlacement == .sidebarTrailing ? .leading : .trailing) {
            Rectangle().fill(Color.primary.opacity(0.1)).frame(width: 1)
        }
    }
}

private struct BookmarksStrip: View {
    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "book").font(.system(size: 10)).foregroundStyle(.secondary)
            ForEach(0..<6, id: \.self) { _ in
                Capsule().fill(Color.primary.opacity(0.14)).frame(width: 46, height: 5)
            }
            Spacer()
        }
        .padding(.horizontal, 12)
        .frame(height: 22)
        .accessibilityHidden(true)
    }
}

private struct PagePlaceholder: View {
    @Environment(\.shellTheme) private var theme
    let layout: MosaicLayout

    var body: some View {
        let inset = max(layout.appearance.contentInset, 6)
        let radius = max(layout.appearance.contentCornerRadius, 8)
        ZStack(alignment: .topLeading) {
            ShellColors.page
            VStack(alignment: .leading, spacing: 9) {
                Capsule().fill(Color(hex: layout.appearance.accent).opacity(0.5)).frame(width: 150, height: 10)
                ForEach(0..<4, id: \.self) { index in
                    Capsule().fill(Color.primary.opacity(0.09)).frame(width: CGFloat(320 - index * 46), height: 6)
                }
                Text("Your pages keep running while you edit: nothing is reloaded.")
                    .font(.caption).foregroundStyle(.tertiary).padding(.top, 6)
            }
            .padding(20)
        }
        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
        .overlay {
            if layout.appearance.glow {
                RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(Color(hex: layout.appearance.accent), lineWidth: 1.5)
            }
        }
        .padding(inset)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityHidden(true)
    }
}

// MARK: - Regions

/// One region of the layout: its components as chips, a drop target with an insertion
/// indicator, and a red refusal when the dragged component can't go here.
private struct RegionZone: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    let region: ShellRegion
    let axis: Axis
    var leadingInset: CGFloat = 0

    @State private var frames: [Int: CGRect] = [:]
    @State private var dropIndex: Int?
    @State private var verdict: CanvasVerdict?

    var body: some View {
        let canvas = model.canvas
        let items = canvas.layout.components(in: region)
        let space = "region-\(region.rawValue)"
        let ids = Self.ids(for: items)
        let stack = axis == .horizontal
            ? AnyLayout(HStackLayout(spacing: 4))
            : AnyLayout(VStackLayout(spacing: 4))
        ZStack(alignment: axis == .horizontal ? .leading : .top) {
            stack {
                ForEach(Array(zip(items.indices, ids)), id: \.1) { index, _ in
                    PlacedChip(slot: CanvasSlot(region, index), component: items[index], axis: axis, count: items.count)
                        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named(space)) } action: { frames[index] = $0 }
                }
                if axis == .vertical { Spacer(minLength: 0) }
            }
            .frame(maxWidth: .infinity, maxHeight: axis == .vertical ? .infinity : nil,
                   alignment: axis == .horizontal ? .leading : .top)
            if items.isEmpty {
                Text(axis == .vertical ? "Rail" : "\(region.title) — drop here")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .rotationEffect(axis == .vertical ? .degrees(-90) : .zero)
                    .fixedSize()
                    .frame(maxWidth: .infinity, minHeight: 30, maxHeight: axis == .vertical ? .infinity : 30)
                    .allowsHitTesting(false)
            }
        }
        .padding(.leading, leadingInset)
        .padding(5)
        .frame(maxWidth: .infinity, maxHeight: axis == .vertical ? .infinity : nil)
        .coordinateSpace(.named(space))
        .background {
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .fill(zoneFill)
                .overlay {
                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .strokeBorder(zoneStroke, style: StrokeStyle(lineWidth: verdict == nil ? 1 : 2, dash: items.isEmpty || verdict != nil ? [4, 3] : []))
                }
                .padding(2)
        }
        .overlay { indicator(count: items.count) }
        .overlay(alignment: .topTrailing) {
            if let message = verdict?.message {
                Image(systemName: "nosign")
                    .foregroundStyle(.red)
                    .padding(6)
                    .help(message)
                    .accessibilityLabel(message)
            }
        }
        .onDrop(of: [.utf8PlainText, .plainText], delegate: RegionDropDelegate(
            region: region, axis: axis, frames: frames, count: items.count, canvas: canvas,
            dropIndex: $dropIndex, verdict: $verdict))
        .onChange(of: items) { _, new in frames = frames.filter { $0.key < new.count } }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(region.title)
    }

    /// Stable identities: the component, numbered for repeated flexible spaces.
    static func ids(for items: [ShellComponent]) -> [String] {
        var seen: [ShellComponent: Int] = [:]
        return items.map { component in
            let n = seen[component, default: 0]
            seen[component] = n + 1
            return "\(component.rawValue)#\(n)"
        }
    }

    private var zoneFill: Color {
        switch verdict {
        case .allowed: theme.accent.opacity(0.12)
        case .rejected: Color.red.opacity(0.10)
        case nil: Color.primary.opacity(0.025)
        }
    }

    private var zoneStroke: Color {
        switch verdict {
        case .allowed: theme.accent.opacity(0.8)
        case .rejected: Color.red.opacity(0.75)
        case nil: Color.primary.opacity(0.12)
        }
    }

    @ViewBuilder private func indicator(count: Int) -> some View {
        if let index = dropIndex, verdict == .allowed {
            GeometryReader { proxy in
                let position = insertionPosition(index: index, count: count, size: proxy.size)
                if axis == .horizontal {
                    Capsule().fill(theme.accent)
                        .frame(width: 3, height: max(proxy.size.height - 12, 10))
                        .position(x: position, y: proxy.size.height / 2)
                } else {
                    Capsule().fill(theme.accent)
                        .frame(width: max(proxy.size.width - 12, 10), height: 3)
                        .position(x: proxy.size.width / 2, y: position)
                }
            }
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
    }

    private func insertionPosition(index: Int, count: Int, size: CGSize) -> CGFloat {
        let horizontal = axis == .horizontal
        if count == 0 || frames.isEmpty { return horizontal ? 10 + leadingInset : 10 }
        if index < count, let frame = frames[index] { return (horizontal ? frame.minX : frame.minY) - 2 }
        if let last = frames[count - 1] { return (horizontal ? last.maxX : last.maxY) + 2 }
        return horizontal ? size.width - 8 : size.height - 8
    }
}

private struct RegionDropDelegate: DropDelegate {
    let region: ShellRegion
    let axis: Axis
    let frames: [Int: CGRect]
    let count: Int
    let canvas: CanvasController
    @Binding var dropIndex: Int?
    @Binding var verdict: CanvasVerdict?

    private func index(at location: CGPoint) -> Int {
        (0..<count).filter { i in
            guard let frame = frames[i] else { return false }
            return axis == .horizontal ? frame.midX < location.x : frame.midY < location.y
        }.count
    }

    func validateDrop(info: DropInfo) -> Bool { canvas.dragging != nil }

    func dropEntered(info: DropInfo) { update(info) }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        update(info)
        return DropProposal(operation: verdict?.isAllowed == true ? .move : .forbidden)
    }

    func dropExited(info: DropInfo) {
        dropIndex = nil
        verdict = nil
    }

    func performDrop(info: DropInfo) -> Bool {
        defer {
            dropIndex = nil
            verdict = nil
        }
        guard let item = canvas.dragging else { return false }
        let result = canvas.verdict(for: item, in: region)
        guard result.isAllowed else {
            if let message = result.message { canvas.say(message, error: true) }
            canvas.dragging = nil
            return false
        }
        canvas.drop(item, in: region, before: index(at: info.location))
        return true
    }

    private func update(_ info: DropInfo) {
        guard let item = canvas.dragging else { return }
        let result = canvas.verdict(for: item, in: region)
        if verdict != result {
            verdict = result
            if let message = result.message { canvas.say(message, error: true) }
        }
        dropIndex = index(at: info.location)
    }
}

// MARK: - Chips

/// A component on the canvas: select by clicking, drag to move, context menu and
/// accessibility actions for everything a drag does.
private struct PlacedChip: View {
    @EnvironmentObject private var model: BrowserModel
    let slot: CanvasSlot
    let component: ShellComponent
    let axis: Axis
    let count: Int

    var body: some View {
        let canvas = model.canvas
        let layout = canvas.layout
        let selected = canvas.selection == slot
        ComponentChip(component: component, style: axis == .vertical ? .column : .row,
                      selected: selected, lifted: selected && canvas.grab != nil,
                      drawn: LayoutCanvasRules.isDrawn(component, in: layout))
            .onTapGesture { canvas.selection = slot; canvas.message = nil }
            .onDrag {
                canvas.selection = slot
                canvas.dragging = .placed(slot, component)
                return NSItemProvider(object: "mosaic-canvas:\(component.rawValue)" as NSString)
            } preview: {
                ComponentChip(component: component, style: .palette, selected: false, lifted: true, drawn: true)
            }
            .contextMenu { menu(canvas: canvas, layout: layout) }
            .help(component.title)
            .accessibilityElement()
            .accessibilityLabel(component.title)
            .accessibilityValue("\(slot.region.title), \(slot.index + 1) of \(count)")
            .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
            .accessibilityAction { canvas.selection = slot }
            .accessibilityAction(named: "Move left") { canvas.selection = slot; canvas.moveSelected(.left) }
            .accessibilityAction(named: "Move right") { canvas.selection = slot; canvas.moveSelected(.right) }
            .accessibilityAction(named: "Move to the row above") { canvas.selection = slot; canvas.moveSelected(.up) }
            .accessibilityAction(named: "Move to the row below") { canvas.selection = slot; canvas.moveSelected(.down) }
            .accessibilityAction(named: "Remove") { canvas.remove(slot) }
    }

    @ViewBuilder private func menu(canvas: CanvasController, layout: MosaicLayout) -> some View {
        Button("Move Left") { canvas.selection = slot; canvas.moveSelected(.left) }
            .disabled(slot.index == 0)
        Button("Move Right") { canvas.selection = slot; canvas.moveSelected(.right) }
            .disabled(slot.index >= count - 1)
        MoveToMenu(slot: slot, component: component)
        Divider()
        Button("Remove", role: .destructive) { canvas.remove(slot) }
            .disabled(!LayoutCanvasRules.verdict(removing: slot, layout: layout).isAllowed)
    }
}

/// "Move to ▸" every region, with the ones that refuse the component disabled.
private struct MoveToMenu: View {
    @EnvironmentObject private var model: BrowserModel
    let slot: CanvasSlot
    let component: ShellComponent

    var body: some View {
        let canvas = model.canvas
        Menu("Move To") {
            ForEach(LayoutCanvasRules.regionOrder, id: \.self) { region in
                let verdict = LayoutCanvasRules.verdict(placing: component, in: region, layout: canvas.layout, from: slot)
                Button(region.title) {
                    canvas.move(slot, to: region, before: canvas.layout.components(in: region).count)
                }
                .disabled(!verdict.isAllowed || region == slot.region)
                .help(verdict.message ?? "")
            }
        }
    }
}

/// The look of a component on the canvas and in the palette.
struct ComponentChip: View {
    enum Style { case row, column, palette }

    @Environment(\.shellTheme) private var theme
    let component: ShellComponent
    let style: Style
    let selected: Bool
    let lifted: Bool
    let drawn: Bool

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        content
            .font(.system(size: 12.5, weight: .medium))
            .foregroundStyle(Color.primary.opacity(drawn ? 0.85 : 0.4))
            .padding(.horizontal, style == .column ? 0 : 9)
            .frame(minWidth: style == .column ? 40 : 32, maxWidth: style != .palette && component.isFlexible ? .infinity : nil)
            .frame(height: style == .palette ? 32 : 30)
            .background {
                if component == .flexibleSpace && style != .palette {
                    shape.strokeBorder(Color.primary.opacity(0.25), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
                } else {
                    shape.fill(Color(nsColor: .controlBackgroundColor).opacity(component == .addressField ? 0.95 : 0.85))
                        .overlay(shape.strokeBorder(Color.primary.opacity(0.1)))
                }
            }
            .overlay {
                if selected { shape.strokeBorder(theme.accent, lineWidth: 2) }
            }
            .shadow(color: .black.opacity(lifted ? 0.28 : 0.05), radius: lifted ? 6 : 1, y: lifted ? 3 : 0.5)
            .scaleEffect(lifted ? 1.06 : 1)
            .animation(theme.animation(.snappy(duration: 0.18)), value: lifted)
            .contentShape(shape)
    }

    @ViewBuilder private var content: some View {
        switch (component, style) {
        case (.addressField, .row), (.addressField, .palette):
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                Text("Address Field").foregroundStyle(.secondary).lineLimit(1)
                Spacer(minLength: 0)
            }
        case (.tabStrip, .row):
            HStack(spacing: 4) {
                ForEach(0..<3, id: \.self) { index in
                    RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(index == 0 ? 0.2 : 0.08))
                        .frame(width: 56, height: 18)
                }
                Text(drawn ? "Tabs" : "Tab strip (tabs are in the sidebar)").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Spacer(minLength: 0)
            }
        case (.flexibleSpace, .row):
            Image(systemName: "arrow.left.and.right").font(.system(size: 10)).foregroundStyle(.tertiary)
                .frame(maxWidth: .infinity)
        case (_, .palette):
            HStack(spacing: 7) {
                Image(systemName: component.symbol).frame(width: 18)
                Text(component.title).lineLimit(1)
            }
        default:
            Image(systemName: component.symbol)
        }
    }
}

// MARK: - Status and selection

private struct StatusLine: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        let canvas = model.canvas
        HStack(spacing: 8) {
            if let message = canvas.message {
                Image(systemName: message.isError ? "exclamationmark.triangle.fill" : "info.circle")
                    .foregroundStyle(message.isError ? Color.orange : Color.secondary)
                    .accessibilityHidden(true)
                Text(message.text)
                    .font(.callout)
                    .foregroundStyle(message.isError ? .primary : .secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("Arrow keys select · ⌥ arrow keys move · Return picks up and drops · Delete removes · ⌘Z undoes")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .frame(minHeight: 20)
        .animation(nil, value: canvas.message)
    }
}

private struct SelectionInspector: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme

    var body: some View {
        let canvas = model.canvas
        let layout = canvas.layout
        if let slot = canvas.selection, let component = canvas.component(at: slot) {
            let count = layout.components(in: slot.region).count
            HStack(spacing: 10) {
                Image(systemName: component.symbol)
                    .frame(width: 30, height: 30)
                    .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(theme.accent.opacity(0.15)))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 1) {
                    Text(component.title).font(.headline)
                    Text("\(slot.region.title) · \(slot.index + 1) of \(count)").font(.caption).foregroundStyle(.secondary)
                }
                Spacer(minLength: 8)
                Button { canvas.moveSelected(.left) } label: { Image(systemName: "chevron.left") }
                    .disabled(slot.index == 0)
                    .help("Move left (⌥←)")
                    .accessibilityLabel("Move left")
                Button { canvas.moveSelected(.right) } label: { Image(systemName: "chevron.right") }
                    .disabled(slot.index >= count - 1)
                    .help("Move right (⌥→)")
                    .accessibilityLabel("Move right")
                MoveToMenu(slot: slot, component: component)
                    .fixedSize()
                Button("Remove", role: .destructive) { canvas.remove(slot) }
                    .disabled(!LayoutCanvasRules.verdict(removing: slot, layout: layout).isAllowed)
                    .help(LayoutCanvasRules.verdict(removing: slot, layout: layout).message ?? "Back to Available Items (Delete)")
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.04)))
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Selected item")
        }
    }
}

// MARK: - Palette

private struct CanvasPalette: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @State private var targeted = false

    var body: some View {
        let canvas = model.canvas
        let palette = LayoutCanvasRules.palette(canvas.layout)
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Available Items").font(.headline)
                Text("Drag into the window, or double-click to add. Drop items here to remove them.")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
            }
            FlowLayout(spacing: 8, lineSpacing: 8) {
                ForEach(palette, id: \.self) { component in
                    ComponentChip(component: component, style: .palette, selected: false, lifted: false, drawn: true)
                        .onTapGesture(count: 2) { canvas.add(component) }
                        .onDrag {
                            canvas.dragging = .palette(component)
                            return NSItemProvider(object: "mosaic-canvas:\(component.rawValue)" as NSString)
                        }
                        .contextMenu {
                            Menu("Add To") {
                                ForEach(LayoutCanvasRules.regionOrder, id: \.self) { region in
                                    Button(region.title) { canvas.add(component, to: region) }
                                        .disabled(!LayoutCanvasRules.verdict(placing: component, in: region, layout: canvas.layout).isAllowed)
                                }
                            }
                        }
                        .help("\(component.title): drag into the window or double-click to add")
                        .accessibilityElement()
                        .accessibilityLabel(component.title)
                        .accessibilityHint("Available. Double-click or use the Add action to place it.")
                        .accessibilityAddTraits(.isButton)
                        .accessibilityAction(named: "Add") { canvas.add(component) }
                }
            }
        }
        .padding(12)
        .background {
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(targeted ? Color.red.opacity(0.07) : Color.primary.opacity(0.035))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(targeted ? Color.red.opacity(0.5) : .clear, style: StrokeStyle(lineWidth: 1.5, dash: [5, 4])))
        }
        .onDrop(of: [.utf8PlainText, .plainText], delegate: PaletteDropDelegate(canvas: canvas, targeted: $targeted))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Available items")
    }
}

private struct PaletteDropDelegate: DropDelegate {
    let canvas: CanvasController
    @Binding var targeted: Bool

    private var placed: (CanvasSlot, ShellComponent)? {
        if case .placed(let slot, let component) = canvas.dragging { return (slot, component) }
        return nil
    }

    func validateDrop(info: DropInfo) -> Bool { placed != nil }

    func dropEntered(info: DropInfo) {
        targeted = true
        if let (slot, _) = placed, let message = LayoutCanvasRules.verdict(removing: slot, layout: canvas.layout).message {
            canvas.say(message, error: true)
        }
    }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        guard let (slot, _) = placed else { return DropProposal(operation: .forbidden) }
        return DropProposal(operation: LayoutCanvasRules.verdict(removing: slot, layout: canvas.layout).isAllowed ? .move : .forbidden)
    }

    func dropExited(info: DropInfo) { targeted = false }

    func performDrop(info: DropInfo) -> Bool {
        targeted = false
        guard let item = canvas.dragging else { return false }
        canvas.dropOnPalette(item)
        return true
    }
}

// MARK: - Save popover

struct SaveConfigurationPopover: View {
    @EnvironmentObject private var model: BrowserModel
    @Binding var isPresented: Bool
    @State private var name = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Save Configuration").font(.headline)
            Text("Saves this layout and its appearance (colours, background) under a name.")
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            TextField("Name", text: $name, prompt: Text(model.canvas.layout.name))
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit(save)
            HStack {
                Spacer()
                Button("Cancel") { isPresented = false }
                Button("Save", action: save).buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 300)
        .onAppear { focused = true }
    }

    private func save() {
        let chosen = name.trimmingCharacters(in: .whitespaces).isEmpty ? model.canvas.layout.name : name
        model.canvas.saveCurrent(as: chosen)
        isPresented = false
    }
}

// MARK: - Flow layout

/// Lays its children out in rows, wrapping when a row is full.
struct FlowLayout: Layout {
    var spacing: CGFloat = 8
    var lineSpacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, lineHeight: CGFloat = 0, maxX: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > 0, x + size.width > width {
                x = 0
                y += lineHeight + lineSpacing
                lineHeight = 0
            }
            x += size.width + spacing
            maxX = max(maxX, x - spacing)
            lineHeight = max(lineHeight, size.height)
        }
        return CGSize(width: proposal.width ?? maxX, height: y + lineHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, lineHeight: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > bounds.minX, x + size.width > bounds.maxX {
                x = bounds.minX
                y += lineHeight + lineSpacing
                lineHeight = 0
            }
            view.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            lineHeight = max(lineHeight, size.height)
        }
    }
}
