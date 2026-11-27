import AppKit
import MosaicCore
import Observation

/// State and actions of Settings › Mosaics' editors: the layout canvas, the Gradient Studio
/// and named configurations. Every change goes through `MosaicStore.replaceActiveLayout`,
/// so the real window updates live and no page is reloaded (the shell only swaps the value
/// its views read). Owner: **canvas**.
@MainActor
@Observable
final class CanvasController {
    /// The Mosaics page's modes.
    enum Mode: String, CaseIterable, Identifiable {
        case presets, layout, gradient, configurations
        var id: String { rawValue }
        var title: String {
            switch self {
            case .presets: "Presets"
            case .layout: "Customise Layout"
            case .gradient: "Gradient Studio"
            case .configurations: "Configurations"
            }
        }
    }

    /// What is being dragged on the canvas (read by drop targets to accept or refuse).
    enum DragItem: Equatable {
        case placed(CanvasSlot, ShellComponent)
        case palette(ShellComponent)

        var component: ShellComponent {
            switch self {
            case .placed(_, let c), .palette(let c): c
            }
        }
    }

    /// Keyboard "pick up and move" state: arrows move the component until Return drops it
    /// or Esc puts everything back.
    struct Grab: Equatable {
        var start: MosaicLayout
        var component: ShellComponent
    }

    struct Message: Equatable {
        var text: String
        var isError: Bool
    }

    /// Which background the Gradient Studio edits.
    enum GradientTarget: String, CaseIterable, Identifiable {
        case window, newTab, workspace
        var id: String { rawValue }
        var title: String {
            switch self {
            case .window: "Window"
            case .newTab: "New Tab Page"
            case .workspace: "This Workspace"
            }
        }
    }

    struct ImportOutcome: Equatable {
        var file: String
        var name: String?
        var messages: [String]
        var error: String?
    }

    @ObservationIgnored private unowned let model: BrowserModel
    private var mosaics: MosaicStore { model.mosaics }

    var mode: Mode = .presets
    var selection: CanvasSlot?
    var grab: Grab?
    var dragging: DragItem?
    var message: Message?
    var gradientTarget: GradientTarget = .window
    /// Automation: asks the open Gradient Studio to select colour `n` (as a click would).
    var studioSelectionRequest: Int?
    private(set) var library: ConfigurationLibrary
    private(set) var lastImport: ImportOutcome?
    private var histories: [MosaicPresetID: EditHistory<MosaicLayout>] = [:]
    @ObservationIgnored private var lastRecord: (key: String, at: Date)?
    @ObservationIgnored private let libraryURL: URL

    init(model: BrowserModel,
         libraryURL: URL = AppPaths.support.appendingPathComponent("Canvas/configurations.json")) {
        self.model = model
        self.libraryURL = libraryURL
        library = ConfigurationLibrary.load(from: libraryURL)
    }

    var layout: MosaicLayout { mosaics.layout }

    // MARK: History

    private var history: EditHistory<MosaicLayout> {
        get { histories[mosaics.activePreset] ?? EditHistory(limit: 100) }
        set { histories[mosaics.activePreset] = newValue }
    }

    var canUndo: Bool { history.canUndo }
    var canRedo: Bool { history.canRedo }
    var undoDepth: Int { history.undoStack.count }

    /// Applies `newLayout` to the window (made usable), remembering the previous one for
    /// undo. Changes with the same `coalesce` key within a second share one undo step
    /// (slider drags, colour chart drags).
    func apply(_ newLayout: MosaicLayout, coalesce: String? = nil) {
        let current = layout
        let usable = LayoutCanvasRules.usable(newLayout)
        guard usable != current else { return }
        let now = Date()
        if let key = coalesce, let last = lastRecord, last.key == key, now.timeIntervalSince(last.at) < 1 {
            // Same gesture: keep the first undo point.
        } else {
            history.record(current)
        }
        lastRecord = coalesce.map { ($0, now) }
        mosaics.replaceActiveLayout(usable)
    }

    func undo() {
        endGrab(commit: true)
        guard let previous = history.undo(current: layout) else { return }
        lastRecord = nil
        mosaics.replaceActiveLayout(previous)
        validateSelection()
        say("Undone.", error: false)
    }

    func redo() {
        endGrab(commit: true)
        guard let next = history.redo(current: layout) else { return }
        lastRecord = nil
        mosaics.replaceActiveLayout(next)
        validateSelection()
        say("Redone.", error: false)
    }

    /// The active preset back to its factory layout and look (undoable).
    func resetToPreset() {
        endGrab(commit: true)
        let preset = mosaics.activePreset
        apply(.preset(preset))
        selection = nil
        say("\(preset.title) restored to its defaults. Undo brings your changes back.", error: false)
    }

    // MARK: Layout edits

    private func commit(_ edit: CanvasEdit, record: Bool = true) {
        switch edit {
        case .changed(let newLayout, let newSelection):
            if record {
                apply(newLayout)
            } else {
                mosaics.replaceActiveLayout(newLayout)
            }
            selection = newSelection
            message = nil
            announceSelection()
        case .rejected(let text):
            say(text, error: true)
        }
    }

    func move(_ slot: CanvasSlot, to region: ShellRegion, before index: Int) {
        commit(LayoutCanvasRules.move(slot, to: region, before: index, in: layout))
    }

    func add(_ component: ShellComponent, to region: ShellRegion? = nil, before index: Int? = nil) {
        guard let target = region ?? LayoutCanvasRules.suggestedRegion(for: component, in: layout) else {
            say("There is no row that can take the \(component.title.lowercased()).", error: true)
            return
        }
        commit(LayoutCanvasRules.add(component, to: target, before: index, in: layout))
    }

    func remove(_ slot: CanvasSlot) {
        let component = component(at: slot)
        let edit = LayoutCanvasRules.remove(slot, in: layout)
        commit(edit)
        if edit.layout != nil, let component { say("\(component.title) removed; it is in Available Items.", error: false) }
    }

    func component(at slot: CanvasSlot) -> ShellComponent? {
        let list = layout.components(in: slot.region)
        return list.indices.contains(slot.index) ? list[slot.index] : nil
    }

    /// Drops whatever is being dragged into `region` before `index`.
    func drop(_ item: DragItem, in region: ShellRegion, before index: Int) {
        switch item {
        case .placed(let slot, let component):
            // The layout may have changed since the drag began; find the component again.
            let current = component == self.component(at: slot) ? slot : locate(component)
            if let current { move(current, to: region, before: index) }
        case .palette(let component):
            add(component, to: region, before: index)
        }
        dragging = nil
    }

    /// Dropped on the palette: back out of the layout.
    func dropOnPalette(_ item: DragItem) {
        if case .placed(let slot, let component) = item,
           let current = component == self.component(at: slot) ? slot : locate(component) {
            remove(current)
        }
        dragging = nil
    }

    func verdict(for item: DragItem, in region: ShellRegion) -> CanvasVerdict {
        switch item {
        case .placed(let slot, let component):
            return LayoutCanvasRules.verdict(placing: component, in: region, layout: layout, from: slot)
        case .palette(let component):
            return LayoutCanvasRules.verdict(placing: component, in: region, layout: layout)
        }
    }

    func locate(_ component: ShellComponent) -> CanvasSlot? {
        guard let region = layout.region(of: component),
              let index = layout.components(in: region).firstIndex(of: component) else { return nil }
        return CanvasSlot(region, index)
    }

    // MARK: Keyboard

    /// ←/→: the previous or next component (across rows); ↑/↓: the nearest in the row above or below.
    func moveSelection(_ direction: KeyDirection) {
        let slots = LayoutCanvasRules.regionOrder.flatMap { region in
            layout.components(in: region).indices.map { CanvasSlot(region, $0) }
        }
        guard !slots.isEmpty else { return }
        guard let current = selection, let position = slots.firstIndex(of: current) else {
            selection = slots.first
            announceSelection()
            return
        }
        switch direction {
        case .left: selection = slots[max(position - 1, 0)]
        case .right: selection = slots[min(position + 1, slots.count - 1)]
        case .up, .down:
            let order = LayoutCanvasRules.regionOrder
            guard var r = order.firstIndex(of: current.region) else { return }
            repeat { r += direction == .up ? -1 : 1 } while order.indices.contains(r) && layout.components(in: order[r]).isEmpty
            if order.indices.contains(r) {
                let count = layout.components(in: order[r]).count
                selection = CanvasSlot(order[r], min(current.index, count - 1))
            }
        }
        announceSelection()
    }

    enum KeyDirection { case left, right, up, down }

    /// Moves the selected component (⌥-arrows, or arrows while picked up).
    func moveSelected(_ direction: KeyDirection) {
        guard let slot = selection else {
            say("Select an item first (click it, or use the arrow keys).", error: true)
            return
        }
        let edit: CanvasEdit
        switch direction {
        case .left: edit = LayoutCanvasRules.shift(slot, by: -1, in: layout)
        case .right: edit = LayoutCanvasRules.shift(slot, by: 1, in: layout)
        case .up: edit = LayoutCanvasRules.moveToAdjacentRegion(slot, direction: -1, in: layout)
        case .down: edit = LayoutCanvasRules.moveToAdjacentRegion(slot, direction: 1, in: layout)
        }
        // While picked up, the whole move is one undo step (recorded on drop).
        commit(edit, record: grab == nil)
    }

    func removeSelected() {
        guard let slot = selection else { return }
        endGrab(commit: true)
        remove(slot)
    }

    /// Return/Space: pick up the selected component, or drop it.
    func toggleGrab() {
        if grab != nil {
            endGrab(commit: true)
        } else if let slot = selection, let component = component(at: slot) {
            grab = Grab(start: layout, component: component)
            say("Moving \(component.title). Arrow keys move it, Return drops it, Esc cancels.", error: false)
        }
    }

    /// Ends a keyboard move: keep it (one undo step) or put the layout back.
    func endGrab(commit keep: Bool) {
        guard let grab else { return }
        self.grab = nil
        if keep {
            if grab.start != layout {
                history.record(grab.start)
                lastRecord = nil
            }
            say("\(grab.component.title) dropped.", error: false)
        } else {
            mosaics.replaceActiveLayout(grab.start)
            selection = locate(grab.component)
            say("Move cancelled.", error: false)
        }
    }

    private func validateSelection() {
        if let slot = selection, component(at: slot) == nil { selection = nil }
    }

    // MARK: Options (also undoable)

    func setTabPlacement(_ placement: TabPlacement) {
        apply(layout.withTabPlacement(placement))
        validateSelection()
    }

    func set<T>(_ keyPath: WritableKeyPath<MosaicLayout, T>, _ value: T, coalesce: String? = nil) {
        var copy = layout
        copy[keyPath: keyPath] = value
        apply(copy, coalesce: coalesce)
        validateSelection()
    }

    // MARK: Messages

    func say(_ text: String, error: Bool) {
        message = Message(text: text, isError: error)
        announce(text)
    }

    private func announceSelection() {
        guard let slot = selection, let component = component(at: slot) else { return }
        let count = layout.components(in: slot.region).count
        announce("\(component.title), \(slot.region.title), \(slot.index + 1) of \(count)")
    }

    private func announce(_ text: String) {
        guard let window = NSApp.mainWindow ?? NSApp.windows.first(where: \.isVisible) else { return }
        NSAccessibility.post(element: window, notification: .announcementRequested,
                             userInfo: [.announcement: text, .priority: NSAccessibilityPriorityLevel.high.rawValue])
    }

    // MARK: Gradient Studio targets

    var workspaceID: String { model.selectedWorkspaceID.uuidString }
    var workspaceName: String { model.workspaces.first { $0.id == model.selectedWorkspaceID }?.name ?? "Workspace" }

    /// The background the target shows now.
    func background(for target: GradientTarget) -> BackgroundSpec {
        let a = layout.appearance
        switch target {
        case .window: return a.background
        case .newTab: return a.newTabBackground.mode == .window ? a.background(forWorkspace: workspaceID) : a.newTabBackground.custom
        case .workspace: return a.background(forWorkspace: workspaceID)
        }
    }

    /// Whether the target currently shows a gradient of its own (the studio then edits it live).
    func usesGradient(_ target: GradientTarget) -> Bool {
        let a = layout.appearance
        switch target {
        case .window: return a.background.kind == .gradient
        case .newTab: return a.newTabBackground.mode == .custom && a.newTabBackground.custom.kind == .gradient
        case .workspace: return a.workspaceBackgrounds[workspaceID]?.kind == .gradient
        }
    }

    func useGradient(_ gradient: GradientSpec, for target: GradientTarget, coalesce: String? = nil) {
        var copy = layout
        let clean = gradient.clamped()
        switch target {
        case .window:
            copy.appearance.background.kind = .gradient
            copy.appearance.background.gradient = clean
        case .newTab:
            copy.appearance.newTabBackground.mode = .custom
            copy.appearance.newTabBackground.custom.kind = .gradient
            copy.appearance.newTabBackground.custom.gradient = clean
        case .workspace:
            var spec = copy.appearance.workspaceBackgrounds[workspaceID] ?? copy.appearance.background
            spec.kind = .gradient
            spec.gradient = clean
            copy.appearance.workspaceBackgrounds[workspaceID] = spec
        }
        apply(copy, coalesce: coalesce)
    }

    /// The workspace goes back to the window's background.
    func clearWorkspaceBackground() {
        var copy = layout
        copy.appearance.workspaceBackgrounds[workspaceID] = nil
        apply(copy)
    }

    // MARK: Configurations

    /// The saved configuration the window shows unchanged, if any.
    var activeConfiguration: LayoutConfiguration? {
        library.configurations.first { $0.layout == layout && mosaics.activePreset == $0.layout.preset }
    }

    @discardableResult
    func saveCurrent(as name: String) -> LayoutConfiguration? {
        guard let saved = library.save(name: name, layout: layout) else {
            say("You have \(ConfigurationLibrary.maxConfigurations) configurations; delete one first.", error: true)
            return nil
        }
        // The window now shows the configuration under its name.
        apply(saved.layout)
        persist()
        say("Saved “\(saved.name)”.", error: false)
        return saved
    }

    /// Switches to a saved configuration: its preset becomes active with its layout (the
    /// preset keeps that as its edited layout; Restore Defaults returns to the factory one).
    func applyConfiguration(_ id: String) {
        guard let configuration = library.configuration(id) else { return }
        endGrab(commit: true)
        if mosaics.activePreset != configuration.layout.preset {
            mosaics.apply(configuration.layout.preset)
        }
        apply(configuration.layout)
        library.activeID = id
        selection = nil
        persist()
        model.notice = "Mosaic: \(configuration.name)"
        say("Switched to “\(configuration.name)”.", error: false)
    }

    func updateConfiguration(_ id: String) {
        guard library.update(id, layout: layout) else { return }
        if let updated = library.configuration(id) { apply(updated.layout) }
        persist()
        say("Updated “\(library.configuration(id)?.name ?? "")” with the current layout.", error: false)
    }

    func renameConfiguration(_ id: String, to name: String) {
        let wasActive = activeConfiguration?.id == id
        guard library.rename(id, to: name) else {
            say("A configuration needs a name.", error: true)
            return
        }
        if wasActive, let renamed = library.configuration(id) { apply(renamed.layout) }
        persist()
    }

    func deleteConfiguration(_ id: String) {
        let name = library.configuration(id)?.name ?? ""
        guard library.delete(id) else { return }
        persist()
        say("Deleted “\(name)”.", error: false)
    }

    /// Writes a configuration (or, with nil, the current layout) to a `.json` file.
    func export(_ id: String?, to url: URL) throws {
        let configuration = id.flatMap(library.configuration)
        let data = try LayoutTransfer.export(name: configuration?.name ?? layout.name,
                                             layout: configuration?.layout ?? layout)
        try data.write(to: url, options: .atomic)
        say("Exported to \(url.lastPathComponent).", error: false)
    }

    /// Reads a layout file, saves it as a configuration and switches to it. Problems are
    /// reported (`lastImport`); a file that can't be read changes nothing.
    @discardableResult
    func importFile(_ url: URL) -> ImportOutcome {
        let outcome: ImportOutcome
        let data: Data
        do {
            let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
            if size > LayoutTransfer.maxFileSize { throw LayoutTransfer.ImportError.tooLarge(size) }
            data = try Data(contentsOf: url)
        } catch {
            outcome = ImportOutcome(file: url.lastPathComponent, messages: [],
                                    error: (error as? LocalizedError)?.errorDescription ?? "The file couldn’t be opened.")
            finishImport(outcome)
            return outcome
        }
        switch LayoutTransfer.importLayout(data) {
        case .failure(let error):
            outcome = ImportOutcome(file: url.lastPathComponent, messages: [], error: error.errorDescription)
        case .success(let report):
            if let saved = library.save(name: report.name, layout: report.layout) {
                persist()
                applyConfiguration(saved.id)
                outcome = ImportOutcome(file: url.lastPathComponent, name: saved.name, messages: report.messages)
            } else {
                outcome = ImportOutcome(file: url.lastPathComponent, messages: [],
                                        error: "You have \(ConfigurationLibrary.maxConfigurations) configurations; delete one first.")
            }
        }
        finishImport(outcome)
        return outcome
    }

    private func finishImport(_ outcome: ImportOutcome) {
        lastImport = outcome
        if let error = outcome.error {
            say("Couldn’t import \(outcome.file): \(error)", error: true)
        } else {
            say("Imported “\(outcome.name ?? outcome.file)”" + (outcome.messages.isEmpty ? "." : " with \(outcome.messages.count) repair\(outcome.messages.count == 1 ? "" : "s")."), error: false)
        }
    }

    func dismissImportReport() { lastImport = nil }

    /// Every preset back to its factory layout (saved configurations are kept).
    func restoreAllPresetDefaults() {
        endGrab(commit: true)
        for preset in MosaicPresetID.allCases where preset != mosaics.activePreset {
            mosaics.restoreDefaults(preset)
        }
        resetToPreset()
        say("All presets restored to their defaults. Your saved configurations are kept.", error: false)
    }

    private func persist() {
        do {
            try library.write(to: libraryURL)
        } catch {
            NSLog("Mosaic: could not save layout configurations: %@", error.localizedDescription)
            say("Couldn’t save configurations: \(error.localizedDescription)", error: true)
        }
    }
}
