import MosaicCore
import SwiftUI

// Mount points and calls the shell makes into the library and platform areas, under
// shell-prefixed names, so the shell's views stay independent of how those areas implement
// them. Same pattern as TabsBridge.swift.

enum FeatureMounts {
    static let libraryAvailable = true
}

extension BrowserModel {
    /// The address bar's suggestions as they arrive (local results first, remote ones later).
    func shellSuggestionStream(for input: String) -> AsyncStream<[Library.Suggestion]> {
        library.suggestionStream(for: input)
    }

    /// Opens a suggestion (switches to the tab for an open-tab row).
    func shellOpen(_ suggestion: Library.Suggestion, inNewTab: Bool = false) {
        library.open(suggestion, inNewTab: inNewTab)
    }

    /// The bookmark showing `url`, for the star's editor.
    func shellBookmarkID(for url: String) -> String? {
        library.bookmarks.first { $0.url == url }?.id
    }

    /// A Mozilla tab cannot load because no Gecko runtime is installed.
    var shellGeckoRuntimeMissing: Bool { geckoRuntimeMissing }
}

/// The star's popover editor.
struct ShellBookmarkEditorMount: View {
    @EnvironmentObject private var model: BrowserModel
    let bookmarkID: String
    var heading: String?
    var onDone: () -> Void

    var body: some View {
        BookmarkEditor(library: model.library, bookmarkID: bookmarkID, heading: heading, onDone: onDone)
    }
}

/// Toolbar buttons of enabled extensions, beside the Extensions button.
struct ShellExtensionToolbarButtonsMount: View {
    var body: some View { ExtensionToolbarButtons() }
}

/// Shown in a Mozilla tab when no Gecko runtime is installed.
struct ShellGeckoRuntimeMissingMount: View {
    let tab: BrowserTab

    var body: some View { GeckoRuntimeMissingView(tab: tab) }
}
