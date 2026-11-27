import Foundation
import MosaicCore
import Observation

/// The active Mosaic and the user's edited copy of every preset.
///
/// Owner: **shell**. The window is drawn from `effectiveLayout`. Editing goes through
/// `update(_:)` (active layout) or `replaceActiveLayout(_:)` (a whole new layout, e.g. from
/// the layout editor or an import); both normalise and save. Switching presets only swaps
/// the value that the views read, so no page is reloaded and no tab is touched.
@MainActor
@Observable
final class MosaicStore {
    /// The preset whose (edited) layout is in use.
    private(set) var activePreset: MosaicPresetID
    /// The user's version of each preset, starting from the factory layout.
    private(set) var layouts: [MosaicPresetID: MosaicLayout]
    /// Shown instead of the active layout while previewing from Settings › Mosaics.
    private(set) var previewLayout: MosaicLayout?

    @ObservationIgnored private let fileURL: URL

    init(fileURL: URL = AppPaths.support.appendingPathComponent("Shell/mosaics.json")) {
        self.fileURL = fileURL
        var layouts = Dictionary(uniqueKeysWithValues: MosaicPresetID.allCases.map { ($0, MosaicLayout.preset($0)) })
        var active = MosaicPresetID.zenArc
        if let data = try? Data(contentsOf: fileURL),
           let saved = try? JSONDecoder().decode(SavedState.self, from: data) {
            active = saved.active
            for (key, layout) in saved.layouts {
                guard let id = MosaicPresetID(rawValue: key) else { continue }
                layouts[id] = layout.normalized()
            }
        }
        self.layouts = layouts
        self.activePreset = active
    }

    /// The active preset's layout as the user has configured it.
    var layout: MosaicLayout { layouts[activePreset] ?? .preset(activePreset) }

    /// What the window shows right now (the preview while previewing).
    var effectiveLayout: MosaicLayout { previewLayout ?? layout }

    func layout(for preset: MosaicPresetID) -> MosaicLayout { layouts[preset] ?? .preset(preset) }

    /// Whether `preset` differs from its factory layout.
    func isCustomized(_ preset: MosaicPresetID) -> Bool { layout(for: preset) != MosaicLayout.preset(preset) }

    // MARK: Changing

    /// Makes `preset` the active Mosaic (with the user's edits to it).
    func apply(_ preset: MosaicPresetID) {
        previewLayout = nil
        guard preset != activePreset else { return }
        activePreset = preset
        save()
    }

    /// Edits the active layout in place.
    func update(_ change: (inout MosaicLayout) -> Void) {
        var copy = layout
        change(&copy)
        replaceActiveLayout(copy)
    }

    /// Replaces the active preset's layout (layout editor, import).
    func replaceActiveLayout(_ layout: MosaicLayout) {
        let normalized = layout.normalized()
        guard normalized != self.layout else { return }
        layouts[activePreset] = normalized
        save()
    }

    /// Returns `preset` to its factory layout.
    func restoreDefaults(_ preset: MosaicPresetID) {
        layouts[preset] = .preset(preset)
        if previewLayout?.preset == preset { previewLayout = .preset(preset) }
        save()
    }

    /// Shows `preset` in the window without making it active; `endPreview` goes back.
    func preview(_ preset: MosaicPresetID) {
        previewLayout = preset == activePreset ? nil : layout(for: preset)
    }

    func endPreview() { previewLayout = nil }

    var previewedPreset: MosaicPresetID? { previewLayout?.preset }

    // MARK: Persistence

    private struct SavedState: Codable {
        var active: MosaicPresetID
        var layouts: [String: MosaicLayout]
    }

    private func save() {
        let state = SavedState(
            active: activePreset,
            layouts: Dictionary(uniqueKeysWithValues: layouts.map { ($0.key.rawValue, $0.value) }))
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(state)
            try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: fileURL, options: .atomic)
        } catch {
            NSLog("Mosaic: could not save the Mosaic layouts: %@", error.localizedDescription)
        }
    }
}
