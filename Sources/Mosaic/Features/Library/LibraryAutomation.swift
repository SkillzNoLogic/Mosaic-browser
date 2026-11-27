import AppKit
import Foundation
import MosaicCore

/// Library commands for `Automation` (scripted checks). Each writes its answer as JSON to
/// the path given in `out` (or in `value` for commands that take nothing else):
///
///     swift Scripts/drive.swift libraryBookmark [edit]              # bookmark the current page
///     swift Scripts/drive.swift libraryBookmarks /tmp/b.json        # every bookmark
///     swift Scripts/drive.swift libraryHistory /tmp/h.json          # counts and the latest visits
///     swift Scripts/drive.swift librarySuggest "git" /tmp/s.json    # suggestions (after remote ones)
///     swift Scripts/drive.swift libraryImport ~/bookmarks.html      # import a bookmark file
///     swift Scripts/drive.swift libraryExport /tmp/export.html
///     swift Scripts/drive.swift libraryReadingAdd                   # add the current page
///     swift Scripts/drive.swift libraryReadingList /tmp/r.json
///     swift Scripts/drive.swift libraryClearHistory hour|today|all
///     swift Scripts/drive.swift libraryBar show|hide
///     swift Scripts/drive.swift libraryTopSites /tmp/t.json
///     swift Scripts/drive.swift libraryRemoteSuggestions on|off
///     swift Scripts/drive.swift libraryAddEngine "Name|keyword|https://x.test/?q=%s"
///     swift Scripts/drive.swift libraryOpen bookmarks|history|reading-list|search-settings
@MainActor
enum LibraryAutomation {
    static let commands: Set<String> = [
        "libraryBookmark", "libraryBookmarks", "libraryHistory", "librarySuggest", "libraryImport",
        "libraryExport", "libraryReadingAdd", "libraryReadingList", "libraryClearHistory", "libraryBar",
        "libraryTopSites", "libraryRemoteSuggestions", "libraryAddEngine", "libraryOpen",
    ]

    static func handles(_ action: String) -> Bool { commands.contains(action) }

    static func run(_ action: String, value: String, out: String?, model: BrowserModel) {
        let library = model.library
        switch action {
        case "libraryBookmark":
            // "edit" also shows the editor sheet (only when focus changes are allowed: allowFocus true).
            LibraryCommands.bookmarkCurrentPage(model: model, showEditor: value == "edit")
        case "libraryBookmarks":
            write(library.bookmarks.map(json), to: out ?? value)
        case "libraryHistory":
            let store = library.store
            let counts = store.historyCounts()
            write([
                "addresses": counts.addresses, "visits": counts.visits,
                "latest": store.visits(limit: 50).map { visit in
                    ["url": visit.url, "title": visit.title, "time": visit.time.timeIntervalSince1970,
                     "typed": visit.typed] as [String: Any]
                },
                "entries": store.recentHistory(limit: 50).map { entry in
                    ["url": entry.url, "title": entry.title, "visitCount": entry.visitCount,
                     "typedCount": entry.typedCount, "frecency": entry.frecency] as [String: Any]
                },
            ], to: out ?? value)
        case "librarySuggest":
            guard let out else { return }
            Task {
                var rows: [Library.Suggestion] = []
                var updates = 0
                for await list in library.suggestionStream(for: value) {
                    rows = list
                    updates += 1
                }
                write([
                    "input": value, "updates": updates,
                    "local": library.suggestions(for: value).map(json),
                    "final": rows.map(json),
                ], to: out)
            }
        case "libraryImport":
            LibraryCommands.importBookmarks(from: URL(fileURLWithPath: (value as NSString).expandingTildeInPath), model: model)
        case "libraryExport":
            do { try library.exportBookmarks(to: URL(fileURLWithPath: value)) } catch { model.notice = "\(error)" }
        case "libraryReadingAdd":
            LibraryCommands.addCurrentPageToReadingList(model: model)
        case "libraryReadingList":
            write(library.readingList.map { item in
                ["id": item.id, "url": item.url, "title": item.title, "read": item.isRead] as [String: Any]
            }, to: out ?? value)
        case "libraryClearHistory":
            let range: Library.ClearRange = value == "hour" ? .lastHour : value == "today" ? .today : .all
            library.clearHistory(range)
        case "libraryBar":
            library.showsBookmarksBar = value != "hide"
        case "libraryTopSites":
            write(library.topSites(limit: 8).map(json), to: out ?? value)
        case "libraryRemoteSuggestions":
            library.remoteSuggestionsEnabled = value == "on"
        case "libraryAddEngine":
            let parts = value.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
            if parts.count >= 3 {
                library.saveEngine(CustomSearchEngine(name: parts[0], keyword: parts[1], template: parts[2],
                                                      suggestTemplate: parts.count > 3 ? parts[3] : nil))
            }
        case "libraryOpen":
            if value == "search-settings" { model.openSettings(.search) }
            else if let page = InternalPage(rawValue: value) { model.openInternalPage(page) }
        default:
            break
        }
    }

    /// Fields for `Automation`'s `dump`.
    static func dumpFields(_ model: BrowserModel) -> [String: Any] {
        let library = model.library
        let counts = library.store.historyCounts()
        return [
            "bookmarks": library.bookmarks.filter { !$0.isFolder }.count,
            "bookmarksBar": library.bookmarksBar.map(\.displayTitle),
            "showsBookmarksBar": library.showsBookmarksBar,
            "historyAddresses": counts.addresses,
            "historyVisits": counts.visits,
            "readingList": library.readingList.count,
            "selectedIsBookmarked": model.selectedTab.map { library.isBookmarked($0.url) } ?? false,
            "remoteSuggestions": library.remoteSuggestionsEnabled,
            "customEngines": library.customEngines.map { "\($0.keyword)=\($0.name)" },
            "storageProblem": library.storageProblem ?? "",
        ]
    }

    private static func json(_ bookmark: Bookmark) -> [String: Any] {
        ["id": bookmark.id, "parent": bookmark.parentID ?? "", "kind": bookmark.kind.rawValue,
         "title": bookmark.title, "url": bookmark.url ?? "", "position": bookmark.position, "tags": bookmark.tags]
    }

    private static func json(_ suggestion: Library.Suggestion) -> [String: Any] {
        ["kind": "\(suggestion.kind)", "title": suggestion.title, "value": suggestion.value,
         "detail": suggestion.detail, "url": suggestion.url ?? "", "remote": suggestion.isRemote,
         "bookmarked": suggestion.isBookmarked]
    }

    private static func write(_ object: Any, to path: String) {
        guard !path.isEmpty,
              let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
        else { return }
        try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
    }
}
