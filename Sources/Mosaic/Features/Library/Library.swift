import AppKit
import Foundation
import MosaicCore

/// Bookmarks, history, the reading list, custom search engines and the address bar's
/// suggestions. Owner: **library**.
///
/// The shell uses the contract members (`suggestions(for:)`, `recordVisit(url:title:)`,
/// `isBookmarked(_:)`, `toggleBookmark(url:title:)`, `topSites(limit:)`) plus the additions
/// documented in place (`suggestionStream(for:)`, `open(_:inNewTab:)`, `showsBookmarksBar`).
/// Data lives in `Library.sqlite` in the profile folder (`LibraryStore`).
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
        /// Secondary line: the address, or "Search Google", "Switch to tab"…
        var detail: String = ""
        /// The exact address this row opens (for `.search`, the search results page).
        var url: String?
        /// `.openTab` only: the tab to switch to.
        var tabID: UUID?
        /// The address is bookmarked (the shell may show a star).
        var isBookmarked = false
        /// Rows from the search engine's suggestion service (fetched over the network).
        var isRemote = false
    }

    // MARK: Published state (for the library's own views)

    /// Every bookmark and folder.
    @Published private(set) var bookmarks: [Bookmark] = []
    @Published private(set) var readingList: [ReadingListItem] = []
    @Published private(set) var customEngines: [CustomSearchEngine] = []
    /// Bumped whenever history changes, so pages showing it reload.
    @Published private(set) var historyRevision = 0
    /// Set when the database could not be opened; history and bookmarks then last only until quit.
    @Published private(set) var storageProblem: String?

    /// Whether the bookmarks bar is shown (`BookmarksBarView` draws nothing when false).
    @Published var showsBookmarksBar: Bool {
        didSet { AppPaths.defaults.set(showsBookmarksBar, forKey: Keys.showsBar); changed() }
    }

    /// Network search suggestions from the default engine. Off by default: when on, what is
    /// typed in the address bar is sent to the search engine as it is typed.
    @Published var remoteSuggestionsEnabled: Bool {
        didSet { AppPaths.defaults.set(remoteSuggestionsEnabled, forKey: Keys.remoteSuggestions) }
    }

    /// Asked for every tab before recording history; the privacy area sets it so private
    /// tabs never reach history (for example `library.isPrivateTab = { $0.isPrivate }`).
    var isPrivateTab: (BrowserTab) -> Bool = { _ in false }

    /// Set by the model's hook; gives access to open tabs and the search engine.
    weak var model: BrowserModel?

    let store: LibraryStore
    private var bookmarkedKeys: Set<String> = []
    private var historyIndex: [SuggestionRanker.Candidate] = []
    private var indexRefresh: Task<Void, Never>?
    private let writer = DispatchQueue(label: "dev.mosaic.library.writes", qos: .utility)
    private lazy var recorder = HistoryRecorder(
        commit: { [weak self] visit in self?.commitVisit(visit) },
        retitle: { [weak self] url, title in self?.retitle(url: url, title: title) })
    private var observationCount = 0

    private enum Keys {
        static let showsBar = "library.showsBookmarksBar"
        static let remoteSuggestions = "library.remoteSearchSuggestions"
        static let lastFolder = "library.lastBookmarkFolder"
    }

    init() {
        let defaults = AppPaths.defaults
        showsBookmarksBar = defaults.object(forKey: Keys.showsBar) as? Bool ?? true
        remoteSuggestionsEnabled = defaults.bool(forKey: Keys.remoteSuggestions)
        let file = AppPaths.support.appendingPathComponent(LibraryStore.fileName)
        do {
            store = try LibraryStore(fileURL: file)
        } catch {
            NSLog("Mosaic: library database unavailable (%@); using a temporary one.", "\(error)")
            // An in-memory database cannot fail to open.
            store = try! LibraryStore(fileURL: nil)
            storageProblem = "Mosaic could not open its library database (\(error)). Bookmarks and history will not be kept after you quit."
        }
        reloadBookmarks()
        reloadReadingList()
        reloadEngines()
        refreshHistoryIndex(delay: 0)
        let store = store
        if (store.frecencyComputed.map { Date().timeIntervalSince($0) > 86_400 }) ?? true {
            writer.async { store.recomputeFrecency() }
        }
    }

    // MARK: - Contract

    /// Local suggestions for what has been typed so far, best first: history ranked by
    /// frecency, bookmarks, open tabs, and a search row. Instant (in-memory); see
    /// `suggestionStream(for:)` for the complete, asynchronous list.
    func suggestions(for input: String) -> [Suggestion] {
        compose(input, deepHistory: [], remote: [])
    }

    /// Called whenever a tab commits a page. The model's hook goes through `tabDidChange`,
    /// which calls this once per navigation with the final title; `isPrivate` visits are
    /// dropped.
    func recordVisit(url: String, title: String) {
        recordVisit(url: url, title: title, typed: false, isPrivate: false)
    }

    func recordVisit(url: String, title: String, typed: Bool, isPrivate: Bool) {
        guard !isPrivate, LibraryURL.isRecordable(url) else { return }
        let store = store
        writer.async { [weak self] in
            try? store.recordVisit(url: url, title: title, typed: typed)
            DispatchQueue.main.async { self?.historyChanged() }
        }
    }

    /// Whether `url` is bookmarked (drives the address bar's star).
    func isBookmarked(_ url: String) -> Bool {
        !url.isEmpty && bookmarkedKeys.contains(LibraryURL.normalized(url))
    }

    /// Adds or removes a bookmark for the page. New bookmarks go to the folder used last
    /// (the bookmarks bar at first).
    func toggleBookmark(url: String, title: String) {
        guard !url.isEmpty, InternalPage(url: url) == nil else { return }
        if isBookmarked(url) {
            perform { try $0.removeBookmarks(forURL: url) }
        } else {
            addBookmark(url: url, title: title)
        }
    }

    /// Most visited sites (one per site), for the new-tab page.
    func topSites(limit: Int) -> [Suggestion] {
        store.topSites(limit: limit).map { entry in
            Suggestion(title: entry.displayTitle, value: entry.url, kind: .history,
                       detail: LibraryURL.display(entry.url), url: entry.url, isBookmarked: isBookmarked(entry.url))
        }
    }

    // MARK: - Model hook

    /// The model calls this for every change of every tab (both engines, selected or not).
    func tabDidChange(_ tab: BrowserTab, in model: BrowserModel) {
        if self.model !== model { self.model = model }
        let excluded = tab.internalPage != nil || tab.failure != nil || isPrivateTab(tab)
        recorder.observe(id: tab.id, url: tab.url, title: tab.title, isLoading: tab.isLoading,
                         loadRequest: tab.lastLoadRequest, excluded: excluded)
        observationCount += 1
        if observationCount % 200 == 0 { recorder.prune(keeping: Set(model.tabs.map(\.id))) }
    }

    /// The model's `navigate` loaded `tab` from what was typed in the address bar.
    func noteTypedNavigation(in tab: BrowserTab) {
        recorder.noteTyped(id: tab.id, loadRequest: tab.lastLoadRequest)
    }

    /// Address-bar input → address, honouring search keywords ("w swift" → Wikipedia).
    func resolve(_ input: String, searchEngine: SearchEngine) -> URL? {
        SearchShortcuts.resolve(input, searchEngine: searchEngine, custom: customEngines)
    }

    private func commitVisit(_ visit: HistoryRecorder.Visit) {
        recordVisit(url: visit.url, title: visit.title, typed: visit.typed, isPrivate: false)
    }

    private func retitle(url: String, title: String) {
        let store = store
        writer.async { [weak self] in
            store.updateTitle(url: url, title: title)
            DispatchQueue.main.async { self?.historyChanged() }
        }
    }

    /// Commits visits still waiting for their title (call on quit).
    func flushPendingVisits() {
        recorder.flushAll()
        writer.sync {}
        // A sheet still attached when quitting (the bookmark editor, an import panel) keeps
        // Chromium's teardown from finishing; close it first.
        for window in NSApp.windows {
            while let sheet = window.attachedSheet { window.endSheet(sheet) }
        }
    }

    // MARK: - Suggestions (asynchronous)

    /// The address bar's suggestions as they become available: the local list at once, then
    /// again with matches from all of history (searched off the main thread), then again
    /// with the search engine's own suggestions when `remoteSuggestionsEnabled` is on.
    /// Ends after the last update. Cancelling the consuming task (for example SwiftUI's
    /// `.task(id: text)` when the text changes) cancels the lookups and the network request.
    ///
    ///     .task(id: model.addressText) {
    ///         for await rows in model.library.suggestionStream(for: model.addressText) { self.rows = rows }
    ///     }
    func suggestionStream(for input: String) -> AsyncStream<[Suggestion]> {
        AsyncStream { continuation in
            let local = compose(input, deepHistory: [], remote: [])
            continuation.yield(local)
            let terms = input.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !terms.isEmpty else {
                continuation.finish()
                return
            }
            let store = store
            let task = Task { @MainActor [weak self] in
                let deep = await Task.detached(priority: .userInitiated) {
                    store.searchHistory(terms, limit: 40)
                }.value
                guard let self, !Task.isCancelled else { return continuation.finish() }
                continuation.yield(self.compose(input, deepHistory: deep, remote: []))

                if let request = self.remoteRequest(for: input) {
                    // A short pause, so fast typing does not send a request per keystroke.
                    try? await Task.sleep(nanoseconds: 180_000_000)
                    guard !Task.isCancelled else { return continuation.finish() }
                    let remote = await Self.fetchRemote(request)
                    guard !Task.isCancelled else { return continuation.finish() }
                    if !remote.isEmpty { continuation.yield(self.compose(input, deepHistory: deep, remote: remote)) }
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Opens a suggestion: switches to the tab for `.openTab`, otherwise loads its address.
    func open(_ suggestion: Suggestion, inNewTab: Bool = false) {
        guard let model else { return }
        if suggestion.kind == .openTab, let id = suggestion.tabID, let tab = model.tabs.first(where: { $0.id == id }) {
            model.select(tab)
            return
        }
        let target = suggestion.url ?? suggestion.value
        if inNewTab {
            model.newTab(url: target)
        } else if let url = suggestion.url {
            model.isEditingAddress = false
            model.load(url, in: model.selectedTab ?? model.newTab())
            model.focusContent()
        } else {
            model.navigate(suggestion.value)
        }
    }

    private func remoteRequest(for input: String) -> URLRequest? {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard remoteSuggestionsEnabled, text.count >= 2, !AddressResolver.isAddress(text),
              let model, !(model.selectedTab.map(isPrivateTab) ?? false) else { return nil }
        var engine = model.searchEngine
        var terms = text
        if let match = SearchShortcuts.match(text, custom: customEngines) {
            engine = match.engine
            terms = match.terms
        }
        guard let template = SearchShortcuts.suggestTemplate(for: engine, custom: customEngines),
              let url = SearchShortcuts.suggestURL(template: template, query: terms) else { return nil }
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 3)
        request.setValue("application/x-suggestions+json, application/json", forHTTPHeaderField: "Accept")
        // A generic browser identity; some services answer nothing to unknown clients.
        request.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:128.0) Gecko/20100101 Firefox/128.0",
                         forHTTPHeaderField: "User-Agent")
        return request
    }

    /// No cookies, no cache, no credentials: a suggestion request carries only the terms.
    private static let suggestionSession: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieAcceptPolicy = .never
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        return URLSession(configuration: configuration)
    }()

    private nonisolated static func fetchRemote(_ request: URLRequest) async -> [String] {
        guard let (data, response) = try? await suggestionSession.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return [] }
        return SearchShortcuts.parseSuggestions(data)
    }

    /// Builds the list: a keyword search or the plain search row, the best local matches
    /// (history, bookmarks, open tabs), then the search engine's suggestions.
    private func compose(_ input: String, deepHistory: [HistoryEntry], remote: [String]) -> [Suggestion] {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return [] }
        let engine = model?.searchEngine ?? .default
        var rows: [Suggestion] = []

        let keyword = SearchShortcuts.match(text, custom: customEngines)
        if let keyword {
            rows.append(Suggestion(
                title: keyword.terms, value: text, kind: .search, detail: "Search \(keyword.engine.name)",
                url: keyword.engine.searchURL(for: keyword.terms)?.absoluteString))
        }

        var candidates = historyIndex
        candidates += deepHistory.map {
            SuggestionRanker.Candidate(url: $0.url, title: $0.title, frecency: $0.frecency, source: .history)
        }
        for bookmark in bookmarks where !bookmark.isFolder {
            guard let url = bookmark.url else { continue }
            candidates.append(.init(url: url, title: bookmark.title, frecency: 0, source: .bookmark, isBookmarked: true))
        }
        if let model {
            for tab in model.tabs where tab.id != model.selectedTabID && !tab.isStartPage {
                candidates.append(.init(url: tab.url, title: tab.displayTitle, frecency: 0, source: .openTab, tabID: tab.id))
            }
        }
        let ranked = keyword == nil ? SuggestionRanker.rank(text, candidates: candidates, limit: 7) : []
        var local = ranked.map(row)
        // A strong address match ("git" → github.com) goes above the search row.
        if let first = ranked.first, first.matchesHostPrefix, !local.isEmpty { rows.append(local.removeFirst()) }
        if keyword == nil, !AddressResolver.isAddress(text) {
            rows.append(Suggestion(title: text, value: text, kind: .search, detail: "Search \(engine.name)",
                                   url: engine.searchURL(for: text)?.absoluteString))
        }
        rows += local.prefix(6 - min(rows.count, 2))

        let searchEngine = keyword?.engine ?? engine
        let shown = Set(rows.filter { $0.kind == .search }.map { $0.title.lowercased() })
        for phrase in remote.prefix(8) where !shown.contains(phrase.lowercased()) {
            rows.append(Suggestion(title: phrase, value: phrase, kind: .search, detail: "\(searchEngine.name) suggestion",
                                   url: searchEngine.searchURL(for: phrase)?.absoluteString, isRemote: true))
            if rows.filter(\.isRemote).count == 5 { break }
        }
        return rows
    }

    private func row(_ ranked: SuggestionRanker.Ranked) -> Suggestion {
        let candidate = ranked.candidate
        let title = candidate.title.isEmpty ? LibraryURL.display(candidate.url) : candidate.title
        switch candidate.source {
        case .openTab:
            return Suggestion(title: title, value: candidate.url, kind: .openTab, detail: "Switch to tab",
                              url: candidate.url, tabID: candidate.tabID, isBookmarked: isBookmarked(candidate.url))
        case .bookmark:
            return Suggestion(title: title, value: candidate.url, kind: .bookmark,
                              detail: LibraryURL.display(candidate.url), url: candidate.url, isBookmarked: true)
        case .history:
            let bookmarked = candidate.isBookmarked || isBookmarked(candidate.url)
            return Suggestion(title: title, value: candidate.url, kind: bookmarked ? .bookmark : .history,
                              detail: LibraryURL.display(candidate.url), url: candidate.url, isBookmarked: bookmarked)
        }
    }

    // MARK: - History

    private func historyChanged() {
        historyRevision += 1
        refreshHistoryIndex(delay: 1.5)
    }

    /// The in-memory index of the best few thousand addresses the instant suggestions use.
    private func refreshHistoryIndex(delay: TimeInterval) {
        guard indexRefresh == nil || delay == 0 else { return }
        indexRefresh?.cancel()
        let store = store
        indexRefresh = Task { @MainActor [weak self] in
            if delay > 0 { try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000)) }
            let entries = await Task.detached(priority: .utility) { store.topHistory(limit: 4000) }.value
            guard let self, !Task.isCancelled else { return }
            self.historyIndex = entries.map {
                SuggestionRanker.Candidate(url: $0.url, title: $0.title, frecency: $0.frecency, source: .history)
            }
            self.indexRefresh = nil
        }
    }

    enum ClearRange: String, CaseIterable, Identifiable {
        case lastHour, today, all
        var id: String { rawValue }
        var title: String {
            switch self {
            case .lastHour: "Last Hour"
            case .today: "Today"
            case .all: "All History"
            }
        }
        var start: Date? {
            switch self {
            case .lastHour: Date().addingTimeInterval(-3600)
            case .today: Calendar.current.startOfDay(for: Date())
            case .all: nil
            }
        }
    }

    func clearHistory(_ range: ClearRange) {
        clearHistory(since: range.start)
    }

    /// Clears visits from `since` onwards (nil: all history).
    func clearHistory(since: Date?) {
        perform(history: true) { try $0.clearHistory(since: since) }
    }

    func deleteVisits(_ ids: [Int64]) {
        perform(history: true) { try $0.deleteVisits(ids: ids) }
    }

    func deleteHistory(site: String) {
        perform(history: true) { try $0.deleteHistory(site: site) }
    }

    func recentHistory(limit: Int) -> [HistoryEntry] { store.recentHistory(limit: limit) }

    // MARK: - Bookmarks

    var bookmarksBar: [Bookmark] { children(of: Bookmark.barID) }

    func children(of folderID: String) -> [Bookmark] {
        bookmarks.filter { $0.parentID == folderID }.sorted { $0.position < $1.position }
    }

    func bookmark(id: String) -> Bookmark? { bookmarks.first { $0.id == id } }

    /// Folders in tree order with their depth, for folder pickers.
    func folderTree() -> [(folder: Bookmark, depth: Int)] {
        var result: [(Bookmark, Int)] = []
        func walk(_ id: String, _ depth: Int) {
            guard let folder = bookmark(id: id) else { return }
            result.append((folder, depth))
            for child in children(of: id) where child.isFolder { walk(child.id, depth + 1) }
        }
        walk(Bookmark.barID, 0)
        walk(Bookmark.otherID, 0)
        return result
    }

    /// The folder new bookmarks go to: the one used last, or the bookmarks bar.
    var defaultFolderID: String {
        get {
            let saved = AppPaths.defaults.string(forKey: Keys.lastFolder) ?? Bookmark.barID
            return bookmark(id: saved)?.isFolder == true ? saved : Bookmark.barID
        }
        set { AppPaths.defaults.set(newValue, forKey: Keys.lastFolder) }
    }

    @discardableResult
    func addBookmark(url: String, title: String, folderID: String? = nil, tags: [String] = []) -> Bookmark? {
        let folder = folderID ?? defaultFolderID
        let title = title.isEmpty ? LibraryURL.display(url) : title
        return perform { try $0.addBookmark(url: url, title: title, parentID: folder, tags: tags) }
    }

    @discardableResult
    func addFolder(title: String, in parentID: String) -> Bookmark? {
        perform { try $0.addFolder(title: title, parentID: parentID) }
    }

    func updateBookmark(id: String, title: String, url: String?, tags: [String]?, folderID: String?) {
        perform { store in
            try store.updateBookmark(id: id, title: title, url: url, tags: tags)
            if let folderID, store.bookmark(id: id)?.parentID != folderID {
                try store.moveBookmark(id: id, toParent: folderID)
            }
        }
        if let folderID { defaultFolderID = folderID }
    }

    func moveBookmark(id: String, to folderID: String, position: Int? = nil) {
        perform { try $0.moveBookmark(id: id, toParent: folderID, position: position) }
    }

    func deleteBookmarks(_ ids: [String]) {
        perform { store in for id in ids { try store.deleteBookmark(id: id) } }
    }

    func searchBookmarks(_ text: String) -> [Bookmark] { store.searchBookmarks(text) }

    func bookmarks(taggedWith tag: String) -> [Bookmark] {
        bookmarks.filter { $0.tags.contains { $0.caseInsensitiveCompare(tag) == .orderedSame } }
            .sorted { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
    }

    var allTags: [String] {
        var seen: Set<String> = []
        return bookmarks.flatMap(\.tags).filter { seen.insert($0.lowercased()).inserted }
            .sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }

    /// Imports a Netscape bookmark file (any browser's export) or Chrome's `Bookmarks` JSON.
    /// Returns the number of bookmarks added.
    func importBookmarks(from file: URL) throws -> Int {
        let data = try Data(contentsOf: file)
        let items: [ImportedBookmark]
        if let chrome = ChromeBookmarks.parse(data) {
            items = chrome
        } else {
            let html = String(data: data, encoding: .utf8) ?? String(decoding: data, as: UTF8.self)
            guard html.range(of: "<a ", options: .caseInsensitive) != nil
                    || html.range(of: "NETSCAPE-Bookmark-file", options: .caseInsensitive) != nil
            else { throw ImportError.notABookmarkFile }
            items = NetscapeBookmarks.parse(html)
        }
        let added = try store.importBookmarks(items)
        reloadBookmarks()
        return added
    }

    enum ImportError: LocalizedError {
        case notABookmarkFile
        var errorDescription: String? { "This file does not contain bookmarks in a format Mosaic can read." }
    }

    /// Chrome's bookmarks file, when Chrome is installed with a default profile.
    var chromeBookmarksFile: URL? {
        let file = ChromeBookmarks.defaultFile
        return FileManager.default.isReadableFile(atPath: file.path) ? file : nil
    }

    func exportBookmarks(to file: URL) throws {
        try NetscapeBookmarks.export(store.allBookmarks()).write(to: file, atomically: true, encoding: .utf8)
    }

    private func reloadBookmarks() {
        bookmarks = store.allBookmarks()
        bookmarkedKeys = Set(bookmarks.compactMap { $0.url.map(LibraryURL.normalized) })
        changed()
    }

    // MARK: - Reading list

    func addToReadingList(url: String, title: String) {
        guard LibraryURL.isRecordable(url) else { return }
        perform(readingList: true) { try $0.addToReadingList(url: url, title: title) }
    }

    func setRead(_ item: ReadingListItem, _ read: Bool) {
        perform(readingList: true) { try $0.setRead(id: item.id, read: read) }
    }

    func removeFromReadingList(_ item: ReadingListItem) {
        perform(readingList: true) { try $0.removeFromReadingList(id: item.id) }
    }

    func isInReadingList(_ url: String) -> Bool { readingList.contains { $0.url == url } }

    private func reloadReadingList() {
        readingList = store.readingList()
    }

    // MARK: - Search engines

    /// Built-in engines followed by the user's own.
    var allSearchEngines: [SearchEngine] { SearchEngine.builtIn + customEngines.map(\.searchEngine) }

    func saveEngine(_ engine: CustomSearchEngine) {
        perform(engines: true) { try $0.saveCustomEngine(engine) }
        // Keep the default in step if it was the engine being edited.
        if let model, model.searchEngine.id == engine.id { model.searchEngine = engine.searchEngine }
    }

    func deleteEngine(_ engine: CustomSearchEngine) {
        if let model, model.searchEngine.id == engine.id { model.searchEngine = .default }
        perform(engines: true) { try $0.deleteCustomEngine(id: engine.id) }
    }

    /// Whether `keyword` is free (not a built-in "@" keyword or another engine's).
    func isKeywordAvailable(_ keyword: String, excluding id: String?) -> Bool {
        let word = keyword.lowercased()
        guard !word.isEmpty else { return true }
        if SearchEngine.builtIn.contains(where: { SearchShortcuts.keyword(for: $0) == word }) { return false }
        return !customEngines.contains { $0.id != id && $0.keyword.lowercased() == word }
    }

    private func reloadEngines() {
        customEngines = store.customEngines()
        SearchEngine.registered = customEngines.map(\.searchEngine)
    }

    // MARK: - Plumbing

    /// Runs a change against the store on the main thread (these are small), reloads the
    /// affected caches, and reports a failure in the window's notice banner.
    @discardableResult
    private func perform<T>(history: Bool = false, readingList: Bool = false, engines: Bool = false,
                            _ change: (LibraryStore) throws -> T) -> T? {
        defer {
            if history { historyChanged() }
            if readingList { reloadReadingList() }
            if engines { reloadEngines() }
            if !history && !readingList && !engines { reloadBookmarks() }
        }
        do {
            return try change(store)
        } catch {
            model?.notice = "Library: \(error)"
            return nil
        }
    }

    /// The shell reads `isBookmarked` and `showsBookmarksBar` through the model, so tell
    /// the model's observers too.
    private func changed() {
        model?.objectWillChange.send()
    }
}
