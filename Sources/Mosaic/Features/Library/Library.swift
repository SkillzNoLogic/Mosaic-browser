import Foundation

/// Bookmarks, history and the address bar's suggestions.
/// Owner: **library**. The members below are the contract the shell builds against; the
/// owner implements them and may add anything else.
@MainActor
final class Library: ObservableObject {
    /// One row in the address bar's suggestion list.
    struct Suggestion: Identifiable, Hashable {
        enum Kind: Hashable { case history, bookmark, search, openTab }
        let id = UUID()
        let title: String
        /// What to load (or the search terms, for `.search`).
        let value: String
        let kind: Kind
    }

    /// Suggestions for what has been typed so far, best first.
    func suggestions(for input: String) -> [Suggestion] { [] }

    /// Called by the model whenever a tab commits a page, in either engine.
    func recordVisit(url: String, title: String) {}

    /// Whether `url` is bookmarked (drives the address bar's star).
    func isBookmarked(_ url: String) -> Bool { false }

    /// Adds or removes a bookmark for the page.
    func toggleBookmark(url: String, title: String) {}
}
