import Foundation
import Testing
@testable import MosaicCore

private func memoryStore() throws -> LibraryStore { try LibraryStore(fileURL: nil) }

@Suite struct LibraryStoreSchemaTests {
    @Test func freshDatabaseHasRootsAndCurrentVersion() throws {
        let store = try memoryStore()
        #expect(store.userVersion == LibraryStore.schemaVersion)
        let roots = store.allBookmarks().filter { $0.parentID == nil }.map(\.id).sorted()
        #expect(roots == [Bookmark.barID, Bookmark.otherID])
        #expect(store.customEngines().map(\.keyword) == ["w"])
    }

    @Test func reopeningAFileKeepsDataAndDoesNotMigrateTwice() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("library-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent(LibraryStore.fileName)
        do {
            let store = try LibraryStore(fileURL: file)
            try store.addBookmark(url: "https://example.com/", title: "Example")
            try store.recordVisit(url: "https://example.com/", title: "Example")
        }
        let reopened = try LibraryStore(fileURL: file)
        #expect(reopened.bookmarkCount() == 1)
        #expect(reopened.historyCounts().visits == 1)
        #expect(reopened.allBookmarks().filter { $0.parentID == nil }.count == 2)
    }
}

@Suite struct BookmarkStoreTests {
    @Test func addFindAndRemoveByURL() throws {
        let store = try memoryStore()
        try store.addBookmark(url: "https://Example.com/#top", title: "Example", parentID: Bookmark.barID)
        #expect(store.bookmarks(forURL: "https://example.com").count == 1)
        #expect(store.bookmarkedURLKeys().contains(LibraryURL.normalized("https://example.com/")))
        #expect(try store.removeBookmarks(forURL: "https://example.com/") == 1)
        #expect(store.bookmarkCount() == 0)
    }

    @Test func positionsStayContiguous() throws {
        let store = try memoryStore()
        let a = try store.addBookmark(url: "https://a.test/", title: "A", parentID: Bookmark.barID)
        let b = try store.addBookmark(url: "https://b.test/", title: "B", parentID: Bookmark.barID)
        let c = try store.addBookmark(url: "https://c.test/", title: "C", parentID: Bookmark.barID, position: 0)
        #expect(store.children(of: Bookmark.barID).map(\.id) == [c.id, a.id, b.id])
        #expect(store.children(of: Bookmark.barID).map(\.position) == [0, 1, 2])

        try store.moveBookmark(id: c.id, toParent: Bookmark.barID, position: 2)
        #expect(store.children(of: Bookmark.barID).map(\.id) == [a.id, b.id, c.id])

        try store.deleteBookmark(id: a.id)
        #expect(store.children(of: Bookmark.barID).map(\.position) == [0, 1])
    }

    @Test func movingBetweenFoldersAndCycleProtection() throws {
        let store = try memoryStore()
        let outer = try store.addFolder(title: "Outer")
        let inner = try store.addFolder(title: "Inner", parentID: outer.id)
        let page = try store.addBookmark(url: "https://x.test/", title: "X", parentID: Bookmark.barID)

        try store.moveBookmark(id: page.id, toParent: inner.id)
        #expect(store.children(of: inner.id).map(\.id) == [page.id])
        #expect(store.children(of: Bookmark.barID).isEmpty)

        #expect(throws: (any Error).self) { try store.moveBookmark(id: outer.id, toParent: inner.id) }
        #expect(throws: (any Error).self) { try store.moveBookmark(id: Bookmark.barID, toParent: outer.id) }

        // Deleting a folder deletes what is inside it.
        try store.deleteBookmark(id: outer.id)
        #expect(store.bookmark(id: page.id) == nil)
        #expect(store.bookmarkCount() == 0)
    }

    @Test func editingTitleURLAndTags() throws {
        let store = try memoryStore()
        let page = try store.addBookmark(url: "https://old.test/", title: "Old", tags: ["Swift", "swift", " ui "])
        #expect(store.bookmark(id: page.id)?.tags == ["Swift", "ui"])
        try store.updateBookmark(id: page.id, title: "New", url: "https://new.test/", tags: ["docs"])
        let updated = try #require(store.bookmark(id: page.id))
        #expect(updated.title == "New")
        #expect(updated.url == "https://new.test/")
        #expect(updated.tags == ["docs"])
        #expect(store.bookmarks(forURL: "https://old.test/").isEmpty)
        #expect(store.allTags() == ["docs"])
    }

    @Test func searchMatchesTitleURLAndTags() throws {
        let store = try memoryStore()
        try store.addBookmark(url: "https://swift.org/", title: "Swift", tags: ["language"])
        try store.addBookmark(url: "https://developer.apple.com/", title: "Apple Developer", tags: ["docs"])
        #expect(store.searchBookmarks("swift").map(\.title) == ["Swift"])
        #expect(store.searchBookmarks("docs").map(\.title) == ["Apple Developer"])
        #expect(store.searchBookmarks("apple developer").count == 1)
        #expect(store.searchBookmarks("100%").isEmpty)
    }
}

@Suite struct HistoryStoreTests {
    @Test func visitsAggregateAndTitlesUpdate() throws {
        let store = try memoryStore()
        try store.recordVisit(url: "https://example.com/", title: "", typed: true)
        try store.recordVisit(url: "https://example.com/", title: "Example Domain")
        let entry = try #require(store.historyEntry(url: "https://example.com/"))
        #expect(entry.visitCount == 2)
        #expect(entry.typedCount == 1)
        #expect(entry.title == "Example Domain")
        #expect(entry.host == "example.com")
        store.updateTitle(url: "https://example.com/", title: "Renamed")
        #expect(store.historyEntry(url: "https://example.com/")?.title == "Renamed")
    }

    @Test func internalAndBlankPagesAreNotRecorded() throws {
        let store = try memoryStore()
        for url in ["mosaic://history", "about:blank", "data:text/html,hi", "chrome-error://x", ""] {
            try store.recordVisit(url: url, title: "x")
        }
        #expect(store.historyCounts().addresses == 0)
    }

    @Test func clearByTimeRange() throws {
        let store = try memoryStore()
        let now = Date()
        try store.recordVisit(url: "https://old.test/", title: "Old", at: now.addingTimeInterval(-3 * 86_400))
        try store.recordVisit(url: "https://both.test/", title: "Both", at: now.addingTimeInterval(-3 * 86_400))
        try store.recordVisit(url: "https://both.test/", title: "Both", at: now.addingTimeInterval(-60))
        try store.recordVisit(url: "https://new.test/", title: "New", at: now.addingTimeInterval(-30))

        try store.clearHistory(since: now.addingTimeInterval(-3600))
        #expect(store.historyEntry(url: "https://new.test/") == nil)
        let both = try #require(store.historyEntry(url: "https://both.test/"))
        #expect(both.visitCount == 1)
        #expect(both.lastVisit < now.addingTimeInterval(-86_400))
        #expect(store.historyEntry(url: "https://old.test/") != nil)

        try store.clearHistory(since: nil)
        #expect(store.historyCounts().addresses == 0)
        #expect(store.historyCounts().visits == 0)
    }

    @Test func deleteBySiteCoversSubdomainsOnly() throws {
        let store = try memoryStore()
        try store.recordVisit(url: "https://example.com/a", title: "")
        try store.recordVisit(url: "https://www.example.com/b", title: "")
        try store.recordVisit(url: "https://docs.example.com/c", title: "")
        try store.recordVisit(url: "https://notexample.com/", title: "")
        #expect(try store.deleteHistory(site: "example.com") == 3)
        #expect(store.recentHistory(limit: 10).map(\.url) == ["https://notexample.com/"])
    }

    @Test func deletingAVisitUpdatesCounts() throws {
        let store = try memoryStore()
        try store.recordVisit(url: "https://a.test/", title: "A")
        try store.recordVisit(url: "https://a.test/", title: "A")
        let visits = store.visits()
        #expect(visits.count == 2)
        try store.deleteVisits(ids: [visits[0].id])
        #expect(store.historyEntry(url: "https://a.test/")?.visitCount == 1)
        try store.deleteVisits(ids: [visits[1].id])
        #expect(store.historyEntry(url: "https://a.test/") == nil)
    }

    @Test func visitSearchAndPaging() throws {
        let store = try memoryStore()
        let start = Date().addingTimeInterval(-1000)
        for i in 0..<10 {
            try store.recordVisit(url: "https://site\(i).test/", title: "Page \(i)", at: start.addingTimeInterval(Double(i)))
        }
        let first = store.visits(limit: 4)
        #expect(first.map(\.title) == ["Page 9", "Page 8", "Page 7", "Page 6"])
        let next = store.visits(before: first.last!.time, limit: 4)
        #expect(next.first?.title == "Page 5")
        #expect(store.visits(matching: "page 3").map(\.url) == ["https://site3.test/"])
        #expect(store.searchHistory("site7", limit: 5).count == 1)
    }

    @Test func topSitesAreOnePerHost() throws {
        let store = try memoryStore()
        for _ in 0..<5 { try store.recordVisit(url: "https://github.com/", title: "GitHub") }
        for _ in 0..<3 { try store.recordVisit(url: "https://github.com/apple", title: "Apple") }
        try store.recordVisit(url: "https://swift.org/", title: "Swift")
        let sites = store.topSites(limit: 5)
        #expect(sites.map(\.url) == ["https://github.com/", "https://swift.org/"])
    }

    @Test func concurrentUseIsSafe() async throws {
        let store = try memoryStore()
        await withTaskGroup(of: Void.self) { group in
            for i in 0..<8 {
                group.addTask {
                    for j in 0..<50 {
                        try? store.recordVisit(url: "https://t\(i).test/\(j % 5)", title: "T")
                        _ = store.searchHistory("t\(i)", limit: 5)
                    }
                }
            }
        }
        #expect(store.historyCounts().visits == 400)
        #expect(store.historyCounts().addresses == 40)
    }
}

@Suite struct ReadingListStoreTests {
    @Test func addMarkReadRemove() throws {
        let store = try memoryStore()
        let a = try store.addToReadingList(url: "https://a.test/", title: "A", at: Date().addingTimeInterval(-10))
        let b = try store.addToReadingList(url: "https://b.test/", title: "B")
        #expect(store.readingList().map(\.id) == [b.id, a.id])
        try store.setRead(id: b.id, read: true)
        #expect(store.readingList().map(\.id) == [a.id, b.id])
        #expect(store.readingList().last?.isRead == true)
        // Adding again marks it unread and keeps one entry.
        try store.addToReadingList(url: "https://b.test/", title: "")
        #expect(store.readingList().count == 2)
        #expect(store.readingList().first?.title == "B")
        #expect(store.readingList().allSatisfy { !$0.isRead })
        try store.removeFromReadingList(id: a.id)
        #expect(store.readingList().map(\.url) == ["https://b.test/"])
        #expect(store.isInReadingList("https://b.test/"))
    }
}

@Suite struct FrecencyTests {
    @Test func recentAndTypedVisitsScoreHigher() {
        let now = Date()
        let recent = Frecency.score(visitCount: 1, visits: [(now, false)], now: now)
        let old = Frecency.score(visitCount: 1, visits: [(now.addingTimeInterval(-200 * 86_400), false)], now: now)
        let typed = Frecency.score(visitCount: 1, visits: [(now, true)], now: now)
        #expect(recent == 100)
        #expect(old == 10)
        #expect(typed == 200)
        #expect(Frecency.score(visitCount: 0, visits: [], now: now) == 0)
    }

    @Test func frequentBeatsOccasional() {
        let now = Date()
        let visits = (0..<5).map { (time: now.addingTimeInterval(Double(-$0) * 86_400 * 6), typed: false) }
        let frequent = Frecency.score(visitCount: 20, visits: visits, now: now)
        let occasional = Frecency.score(visitCount: 2, visits: Array(visits.prefix(2)), now: now)
        #expect(frequent > occasional)
    }

    @Test func storeKeepsFrecencyOrder() throws {
        let store = try memoryStore()
        let now = Date()
        try store.recordVisit(url: "https://old.test/", title: "", at: now.addingTimeInterval(-100 * 86_400))
        try store.recordVisit(url: "https://old.test/", title: "", at: now.addingTimeInterval(-100 * 86_400))
        try store.recordVisit(url: "https://new.test/", title: "", at: now)
        try store.recordVisit(url: "https://typed.test/", title: "", typed: true, at: now)
        #expect(store.topHistory(limit: 3).map(\.url) == ["https://typed.test/", "https://new.test/", "https://old.test/"])
        store.recomputeFrecency(now: now)
        #expect(store.frecencyComputed != nil)
        #expect(store.topHistory(limit: 1).first?.url == "https://typed.test/")
    }
}

@Suite struct SuggestionRankerTests {
    typealias C = SuggestionRanker.Candidate

    @Test func hostPrefixBeatsHigherFrecencySubstring() {
        let ranked = SuggestionRanker.rank("git", candidates: [
            C(url: "https://example.com/digital", title: "Digital", frecency: 300, source: .history),
            C(url: "https://github.com/", title: "GitHub", frecency: 100, source: .history),
        ], limit: 5)
        #expect(ranked.map(\.candidate.url) == ["https://github.com/", "https://example.com/digital"])
        #expect(ranked.first?.matchesHostPrefix == true)
    }

    @Test func everyWordMustMatch() {
        let ranked = SuggestionRanker.rank("swift testing", candidates: [
            C(url: "https://swift.org/", title: "Swift", frecency: 500, source: .history),
            C(url: "https://developer.apple.com/xcode/swift-testing/", title: "Swift Testing", frecency: 10, source: .history),
        ], limit: 5)
        #expect(ranked.map(\.candidate.title) == ["Swift Testing"])
    }

    @Test func bookmarksGetABoostAndDuplicatesMerge() {
        let ranked = SuggestionRanker.rank("news", candidates: [
            C(url: "https://news.a.test/", title: "A News", frecency: 100, source: .history),
            C(url: "https://news.b.test/", title: "B News", frecency: 30, source: .history),
            C(url: "https://news.b.test/", title: "B News", frecency: 0, source: .bookmark),
        ], limit: 5)
        #expect(ranked.count == 2)
        #expect(ranked.first?.candidate.url == "https://news.b.test/")
        #expect(ranked.first?.candidate.isBookmarked == true)
    }

    @Test func openTabWinsTheMergedRow() {
        let id = UUID()
        let ranked = SuggestionRanker.rank("exa", candidates: [
            C(url: "https://example.com/", title: "Example", frecency: 400, source: .history),
            C(url: "https://example.com", title: "Example", frecency: 0, source: .openTab, tabID: id),
        ], limit: 5)
        #expect(ranked.count == 1)
        #expect(ranked.first?.candidate.source == .openTab)
        #expect(ranked.first?.candidate.tabID == id)
    }

    @Test func emptyInputGivesNothing() {
        #expect(SuggestionRanker.rank("   ", candidates: [
            C(url: "https://a.test/", title: "A", frecency: 1, source: .history),
        ], limit: 5).isEmpty)
    }
}

@Suite struct NetscapeBookmarksTests {
    static let sample = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><H3 ADD_DATE="1700000000" PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
            <DL><p>
                <DT><A HREF="https://swift.org/" ADD_DATE="1700000001" TAGS="lang,apple">Swift &amp; Co</A>
                <DT><H3>Docs</H3>
                <DL><p>
                    <DT><A href='https://developer.apple.com/documentation/' add_date=1700000002>Apple Docs</A>
                    <DD>A description that is ignored
                </DL><p>
            </DL><p>
            <DT><A HREF="https://example.com/?a=1&amp;b=2">Example &#8212; &quot;quoted&quot;</A>
            <dt><h3>Empty</h3>
            <dl><p>
            </dl><p>
        </DL><p>
        """

    @Test func parsesFoldersToolbarTagsAndEntities() throws {
        let items = NetscapeBookmarks.parse(Self.sample)
        #expect(items.count == 3)
        let bar = items[0]
        #expect(bar.isToolbar)
        #expect(bar.title == "Bookmarks bar")
        #expect(bar.children.map(\.title) == ["Swift & Co", "Docs"])
        #expect(bar.children[0].tags == ["lang", "apple"])
        #expect(bar.children[0].added == Date(timeIntervalSince1970: 1_700_000_001))
        #expect(bar.children[1].children.map(\.url) == ["https://developer.apple.com/documentation/"])
        #expect(items[1].url == "https://example.com/?a=1&b=2")
        #expect(items[1].title == "Example \u{2014} \"quoted\"")
        #expect(items[2].isFolder && items[2].children.isEmpty)
    }

    @Test func importsIntoBarAndOtherAndIsIdempotent() throws {
        let store = try memoryStore()
        let items = NetscapeBookmarks.parse(Self.sample)
        #expect(try store.importBookmarks(items) == 3)
        #expect(store.children(of: Bookmark.barID).map(\.title) == ["Swift & Co", "Docs"])
        #expect(store.children(of: Bookmark.otherID).map(\.title) == ["Example \u{2014} \"quoted\"", "Empty"])
        #expect(try store.importBookmarks(items) == 0)
        #expect(store.bookmarkCount() == 3)
    }

    @Test func exportThenImportRoundTrips() throws {
        let store = try memoryStore()
        try store.addBookmark(url: "https://a.test/?x=<1>&y=\"2\"", title: "A <&> \"B\"", parentID: Bookmark.barID,
                              tags: ["one", "two"])
        let folder = try store.addFolder(title: "Folder & Stuff", parentID: Bookmark.barID)
        try store.addBookmark(url: "https://b.test/", title: "B", parentID: folder.id)
        let nested = try store.addFolder(title: "Nested", parentID: Bookmark.otherID)
        try store.addBookmark(url: "https://c.test/", title: "Ünïcödé ✓", parentID: nested.id)
        try store.addBookmark(url: "https://d.test/", title: "", parentID: Bookmark.otherID)

        let html = NetscapeBookmarks.export(store.allBookmarks())
        let copy = try memoryStore()
        #expect(try copy.importBookmarks(NetscapeBookmarks.parse(html)) == 4)

        func shape(_ store: LibraryStore, _ folder: String) -> [String] {
            store.children(of: folder).flatMap { item -> [String] in
                let line = "\(item.kind.rawValue)|\(item.title)|\(item.url ?? "")|\(item.tags.joined(separator: ","))"
                return [line] + (item.isFolder ? shape(store, item.id).map { "  " + $0 } : [])
            }
        }
        #expect(shape(copy, Bookmark.barID) == shape(store, Bookmark.barID))
        #expect(shape(copy, Bookmark.otherID) == shape(store, Bookmark.otherID))
        let original = store.allBookmarks().first { $0.url == "https://b.test/" }!
        let imported = copy.allBookmarks().first { $0.url == "https://b.test/" }!
        #expect(Int(original.created.timeIntervalSince1970) == Int(imported.created.timeIntervalSince1970))
    }
}

@Suite struct ChromeBookmarksTests {
    @Test func parsesRootsAndDates() throws {
        let json = """
            {"roots": {
              "bookmark_bar": {"type": "folder", "name": "Bookmarks bar", "children": [
                {"type": "url", "name": "Swift", "url": "https://swift.org/", "date_added": "13345000000000000"},
                {"type": "folder", "name": "Work", "children": [
                  {"type": "url", "name": "Mail", "url": "https://mail.test/"}]}]},
              "other": {"type": "folder", "name": "Other bookmarks", "children": [
                {"type": "url", "name": "Example", "url": "https://example.com/"}]},
              "synced": {"type": "folder", "name": "Mobile bookmarks", "children": []}
            }, "version": 1}
            """
        let items = try #require(ChromeBookmarks.parse(Data(json.utf8)))
        #expect(items.count == 2)
        #expect(items[0].isToolbar)
        #expect(items[0].children.map(\.title) == ["Swift", "Work"])
        #expect(items[0].children[1].children.first?.url == "https://mail.test/")
        #expect(items[1].url == "https://example.com/")
        let added = try #require(items[0].children[0].added)
        #expect(abs(added.timeIntervalSince1970 - (13_345_000_000 - 11_644_473_600)) < 1)

        let store = try memoryStore()
        #expect(try store.importBookmarks(items) == 3)
        #expect(store.children(of: Bookmark.barID).map(\.title) == ["Swift", "Work"])
    }

    @Test func rejectsOtherJSON() {
        #expect(ChromeBookmarks.parse(Data("{\"a\":1}".utf8)) == nil)
        #expect(ChromeBookmarks.parse(Data("nope".utf8)) == nil)
    }
}

@Suite struct SearchShortcutTests {
    let custom = [CustomSearchEngine(id: "custom.wiki", name: "Wikipedia", keyword: "w",
                                     template: "https://en.wikipedia.org/wiki/Special:Search?search=%s")]

    @Test func keywordSearches() throws {
        let match = try #require(SearchShortcuts.match("w  swift language ", custom: custom))
        #expect(match.engine.name == "Wikipedia")
        #expect(match.terms == "swift language")
        let url = SearchShortcuts.resolve("W swift language", searchEngine: .google, custom: custom)
        #expect(url?.absoluteString == "https://en.wikipedia.org/wiki/Special:Search?search=swift%20language")
    }

    @Test func builtInAtKeywords() {
        let url = SearchShortcuts.resolve("@ddg privacy", searchEngine: .google, custom: custom)
        #expect(url?.host == "duckduckgo.com")
        for engine in SearchEngine.builtIn { #expect(SearchShortcuts.keyword(for: engine) != nil) }
    }

    @Test func notAKeyword() {
        #expect(SearchShortcuts.match("w", custom: custom) == nil)
        #expect(SearchShortcuts.match("w   ", custom: custom) == nil)
        #expect(SearchShortcuts.match("wikipedia swift", custom: custom) == nil)
        #expect(SearchShortcuts.resolve("w3schools.com", searchEngine: .google, custom: custom)?.host == "w3schools.com")
        #expect(SearchShortcuts.resolve("what is swift", searchEngine: .google, custom: custom)?.host == "www.google.com")
    }

    @Test func customEnginesCanBeLookedUpOnceRegistered() {
        let engine = custom[0].searchEngine
        #expect(engine.homepage == "https://en.wikipedia.org/")
        SearchEngine.registered = [engine]
        defer { SearchEngine.registered = [] }
        #expect(SearchEngine.named("custom.wiki") == engine)
    }

    @Test func validation() {
        #expect(CustomSearchEngine.problem(name: "X", keyword: "x", template: "https://x.test/?q=%s") == nil)
        #expect(CustomSearchEngine.problem(name: "", keyword: "x", template: "https://x.test/?q=%s") != nil)
        #expect(CustomSearchEngine.problem(name: "X", keyword: "a b", template: "https://x.test/?q=%s") != nil)
        #expect(CustomSearchEngine.problem(name: "X", keyword: "x", template: "https://x.test/") != nil)
        #expect(CustomSearchEngine.problem(name: "X", keyword: "x", template: "javascript:%s") != nil)
    }

    @Test func suggestionParsingAndURLs() {
        let data = Data(#"["swift",["swift code","swift ui"],[],{"x":1}]"#.utf8)
        #expect(SearchShortcuts.parseSuggestions(data) == ["swift code", "swift ui"])
        #expect(SearchShortcuts.parseSuggestions(Data("{}".utf8)).isEmpty)
        for engine in SearchEngine.builtIn {
            let template = SearchShortcuts.suggestTemplate(for: engine, custom: [])
            #expect(template.flatMap { SearchShortcuts.suggestURL(template: $0, query: "a b&c") }?.scheme == "https")
        }
        let url = SearchShortcuts.suggestURL(template: "https://s.test/?q=%s", query: "a b&c")
        #expect(url?.absoluteString == "https://s.test/?q=a%20b%26c")
    }
}

@Suite struct LibraryURLTests {
    @Test func helpers() {
        #expect(LibraryURL.display("https://www.example.com/") == "example.com")
        #expect(LibraryURL.display("http://example.com/a/") == "example.com/a/")
        #expect(LibraryURL.host(of: "https://WWW.Example.com/x") == "example.com")
        #expect(LibraryURL.normalized("HTTPS://Example.COM/#frag") == LibraryURL.normalized("https://example.com"))
        #expect(LibraryURL.normalized("https://example.com/a") != LibraryURL.normalized("https://example.com/b"))
        #expect(LibraryURL.isRecordable("https://a.test/"))
        #expect(!LibraryURL.isRecordable("mosaic://bookmarks"))
    }
}
