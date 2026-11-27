import MosaicCore
import SwiftUI

// Mount points and calls the shell makes into the library and platform areas whose final
// versions live on `next` but not yet in this branch. Same pattern as TabsBridge.swift.
//
// AFTER MERGING mac/library and mac/platform (integrator): set the flags to true and
// replace each body with the code in its `merged:` comment. Nothing else needs to change.

enum FeatureMounts {
    /// merged: true  (library: suggestion stream, open(_:inNewTab:), BookmarkEditor)
    static let libraryAvailable = false
}

extension BrowserModel {
    /// The address bar's suggestions as they arrive.  merged: `library.suggestionStream(for: input)`
    func shellSuggestionStream(for input: String) -> AsyncStream<[Library.Suggestion]> {
        let rows = library.suggestions(for: input)
        return AsyncStream { continuation in
            continuation.yield(rows)
            continuation.finish()
        }
    }

    /// Opens a suggestion (switches to the tab for an open-tab row).
    /// merged: `library.open(suggestion, inNewTab: inNewTab)`
    func shellOpen(_ suggestion: Library.Suggestion, inNewTab: Bool = false) {
        isEditingAddress = false
        if suggestion.kind == .openTab, let tab = tabs.first(where: { $0.url == suggestion.value }) {
            select(tab)
        } else if inNewTab {
            newTab(url: suggestion.value)
        } else {
            navigate(suggestion.value)
        }
    }

    /// The bookmark showing `url`, for the star's editor.
    /// merged: `library.bookmarks.first { $0.url == url }?.id`
    func shellBookmarkID(for url: String) -> String? { nil }

    /// A Mozilla tab cannot load because no Gecko runtime is installed.
    /// merged: `geckoRuntimeMissing`
    var shellGeckoRuntimeMissing: Bool { gecko.runtime == nil }
}

/// The star's popover editor.
/// merged: body = `BookmarkEditor(library: model.library, bookmarkID: bookmarkID, heading: heading, onDone: onDone)`
struct ShellBookmarkEditorMount: View {
    @EnvironmentObject private var model: BrowserModel
    let bookmarkID: String
    var heading: String?
    var onDone: () -> Void

    var body: some View { EmptyView() }
}

/// Toolbar buttons of enabled extensions, beside the Extensions button.
/// merged: body = `ExtensionToolbarButtons()`
struct ShellExtensionToolbarButtonsMount: View {
    var body: some View { EmptyView() }
}

/// Shown in a Mozilla tab when no Gecko runtime is installed.
/// merged: body = `GeckoRuntimeMissingView(tab: tab)`
struct ShellGeckoRuntimeMissingMount: View {
    let tab: BrowserTab

    var body: some View {
        EngineMessage(
            symbol: "exclamationmark.triangle", title: "The Mozilla engine is not installed",
            detail: "Install it from Settings › Engine, or open this page in Chromium.")
    }
}
