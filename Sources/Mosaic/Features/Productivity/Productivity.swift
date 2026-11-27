import AppKit
import Combine
import MosaicCore

/// The productivity area's services: the command palette, reader mode, customisable
/// keyboard shortcuts and the new-tab Speed Dial. One instance, `BrowserModel.productivity`.
@MainActor
final class Productivity {
    let palette: CommandPalette
    let reader: ReaderMode
    let shortcuts: ShortcutManager
    let speedDial: SpeedDialModel
    let favicons: FaviconCache

    unowned let model: BrowserModel
    private var started = false

    /// Where this area keeps its files in the profile.
    static var directory: URL { AppPaths.support.appendingPathComponent("Productivity", isDirectory: true) }

    init(model: BrowserModel) {
        self.model = model
        palette = CommandPalette(model: model)
        reader = ReaderMode(model: model)
        shortcuts = ShortcutManager(model: model)
        speedDial = SpeedDialModel(store: SpeedDialStore(fileURL: Self.directory.appendingPathComponent("speed-dial.json")))
        favicons = FaviconCache(directory: Self.directory.appendingPathComponent("Favicons", isDirectory: true))
        palette.register("mosaic.builtin") { model in BuiltInPaletteActions.actions(model) }
    }

    /// Called once the main menu exists: applies the user's shortcuts and starts watching tabs.
    func start() {
        guard !started else { return }
        started = true
        shortcuts.start()
        reader.start()
    }

    /// Events the Gecko agent queued for this area (see agent.sys.mjs, "productivity").
    func handleGeckoEvent(_ type: String) {
        let prefix = "productivity.shortcut:"
        guard type.hasPrefix(prefix) else { return }  // "productivity.none": a swallowed key
        shortcuts.perform(commandID: String(type.dropFirst(prefix.count)))
    }
}

extension BrowserModel {
    /// ⌘K.
    func showCommandPalette() {
        productivity.palette.toggle()
    }

    /// ⇧⌘R: reader view for the selected tab, in either engine.
    func toggleReaderMode() {
        guard let tab = selectedTab else { return }
        productivity.reader.toggle(tab)
    }
}
