import Foundation
import Testing
@testable import MosaicCore

@Suite struct BlockerSettingsTests {
    @Test(arguments: [
        ("example.com", "example.com"),
        ("WWW.Example.COM", "example.com"),
        ("https://www.example.com/path?q=1", "example.com"),
        ("news.example.co.uk/article", "news.example.co.uk"),
        ("  example.org  ", "example.org"),
        ("localhost:3000", "localhost"),
    ])
    func normalizesSites(input: String, expected: String) {
        #expect(BlockerSettings.normalize(input) == expected)
    }

    @Test func rejectsThingsThatAreNotSites() {
        #expect(BlockerSettings.normalize("") == nil)
        #expect(BlockerSettings.normalize("about:blank") == nil)
        #expect(BlockerSettings.normalize("two words") == nil)
    }

    @Test func onlyWebPagesHaveASite() {
        #expect(BlockerSettings.site(ofPage: "https://www.example.com/a") == "example.com")
        #expect(BlockerSettings.site(ofPage: "http://localhost:3000/") == "localhost")
        #expect(BlockerSettings.site(ofPage: "about:blank") == nil)
        #expect(BlockerSettings.site(ofPage: "file:///tmp/x.html") == nil)
        #expect(BlockerSettings.site(ofPage: "moz-extension://abc/popup.html") == nil)
        #expect(BlockerSettings.site(ofPage: "") == nil)
    }

    @Test func allowlistCoversSubdomainsButNotLookalikes() {
        let settings = BlockerSettings(allowlist: ["example.com"])
        #expect(settings.isAllowlisted(site: "example.com"))
        #expect(settings.isAllowlisted(site: "news.example.com"))
        #expect(settings.isAllowlisted(site: "a.b.example.com"))
        #expect(!settings.isAllowlisted(site: "notexample.com"))
        #expect(!settings.isAllowlisted(site: "example.com.evil.net"))
    }

    @Test func filtersOnlyWhenEnabledAndNotExempt() {
        var settings = BlockerSettings()
        #expect(settings.filters(pageURL: "https://example.com/"))
        #expect(!settings.filters(pageURL: "about:blank"))

        settings.setAllowlisted(true, site: "https://www.example.com/page")
        #expect(settings.allowlist == ["example.com"])
        #expect(!settings.filters(pageURL: "https://example.com/"))
        #expect(settings.filters(pageURL: "https://other.org/"))

        settings.setAllowlisted(false, site: "example.com")
        #expect(settings.filters(pageURL: "https://example.com/"))

        settings.isEnabled = false
        #expect(!settings.filters(pageURL: "https://other.org/"))
    }

    @Test func blockingIsOnByDefault() {
        #expect(BlockerSettings().isEnabled)
        #expect(BlockerSettings().allowlist.isEmpty)
    }
}

@Suite struct FilterListStoreTests {
    @Test func catalogueMatchesUBlockOriginsDefaults() {
        let ids = FilterListCatalog.defaults.map(\.id)
        #expect(ids == [
            "ublock-filters", "ublock-badware", "ublock-privacy", "ublock-unbreak", "ublock-quick-fixes",
            "easylist", "easyprivacy", "urlhaus-1", "plowe-0",
        ])
        #expect(Set(ids).count == ids.count)
        #expect(FilterListCatalog.defaults.allSatisfy { $0.url.scheme == "https" })
    }

    @Test func recognisesListsAndRejectsErrorPages() {
        let filler = String(repeating: "||ads.example^\n", count: 200)
        #expect(FilterListStore.looksValid(Data("[Adblock Plus 2.0]\n! Title: X\n\(filler)".utf8), isSuffixList: false))
        #expect(FilterListStore.looksValid(Data("! Title: EasyList\n\(filler)".utf8), isSuffixList: false))
        #expect(FilterListStore.looksValid(Data("# hosts\n\(filler)".utf8), isSuffixList: false))
        #expect(!FilterListStore.looksValid(Data("<!DOCTYPE html><html>\(filler)".utf8), isSuffixList: false))
        #expect(!FilterListStore.looksValid(Data("! too short".utf8), isSuffixList: false))
        #expect(FilterListStore.looksValid(
            Data("// This Source Code Form… publicsuffix.org\n\(filler)".utf8), isSuffixList: true))
        #expect(!FilterListStore.looksValid(Data("! Title: X\n\(filler)".utf8), isSuffixList: true))
    }

    @Test func seedsMissingFilesOnlyAndReadsThemBack() throws {
        let root = try Fixtures.temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let bundled = root.appendingPathComponent("bundled", isDirectory: true)
        try FileManager.default.createDirectory(at: bundled, withIntermediateDirectories: true)
        let lists = Array(FilterListCatalog.defaults.prefix(2))
        try Data("psl".utf8).write(to: bundled.appendingPathComponent(FilterListCatalog.suffixListFileName))
        try Data("! first".utf8).write(to: bundled.appendingPathComponent(lists[0].fileName))
        try Data("! second".utf8).write(to: bundled.appendingPathComponent(lists[1].fileName))

        let store = FilterListStore(directory: root.appendingPathComponent("lists"), lists: lists)
        try FileManager.default.createDirectory(at: store.directory, withIntermediateDirectories: true)
        // A list that is already there (say, a newer download) must survive seeding.
        try Data("! newer".utf8).write(to: store.fileURL(for: lists[1]))

        try store.seed(from: bundled)

        #expect(store.suffixList() == "psl")
        #expect(store.loadLists().map(\.text) == ["! first", "! newer"])
        #expect(store.availableListPaths().count == 2)
        #expect(store.oldestModification() != nil)
    }

    @Test func missingListsAreSkipped() throws {
        let root = try Fixtures.temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = FilterListStore(directory: root)
        #expect(store.loadLists().isEmpty)
        #expect(store.suffixList() == nil)
        #expect(store.oldestModification() == nil)
    }
}

/// Runs uBlock Origin's real engine. Needs the bundle Scripts/bootstrap.sh
/// builds in Vendor/ublock; skipped when it is absent (a fresh clone).
@Suite struct BlockerEngineTests {
    static let vendor = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("Vendor/ublock")
    static var bundle: URL { vendor.appendingPathComponent("ublock-engine.js") }
    static var suffixList: URL { vendor.appendingPathComponent("lists/public_suffix_list.dat") }
    static var available: Bool {
        FileManager.default.fileExists(atPath: bundle.path) && FileManager.default.fileExists(atPath: suffixList.path)
    }

    static let filters = """
    ! Title: Mosaic test list
    ||ads.example^
    ||tracker.example^$third-party
    /banner/*$image
    @@||ads.example/allowed.js$script
    example.org##.ad
    """

    private func makeEngine() throws -> BlockerEngine {
        try BlockerEngine(
            bundle: Self.bundle,
            suffixList: try String(contentsOf: Self.suffixList, encoding: .utf8),
            lists: [BlockerEngine.List(name: "test", text: Self.filters)])
    }

    @Test(.enabled(if: available)) func compilesNetworkFiltersOnly() throws {
        let engine = try makeEngine()
        // Four network filters; the cosmetic one (##.ad) is not compiled.
        #expect(engine.filterCount == 4)
        #expect(engine.version.contains("."))
    }

    @Test(.enabled(if: available)) func decidesLikeUBlockOrigin() throws {
        let engine = try makeEngine()
        let page = "https://news.example.com/article"
        #expect(engine.verdict(url: "https://ads.example/x.js", documentURL: page, type: "script") == .block)
        #expect(engine.verdict(url: "https://sub.ads.example/p.gif", documentURL: page, type: "image") == .block)
        #expect(engine.verdict(url: "https://ads.example/allowed.js", documentURL: page, type: "script") == .allow)
        #expect(engine.verdict(url: "https://cdn.example.net/app.js", documentURL: page, type: "script") == .none)
        // $image applies to images only.
        #expect(engine.verdict(url: "https://cdn.example.net/banner/1.png", documentURL: page, type: "image") == .block)
        #expect(engine.verdict(url: "https://cdn.example.net/banner/1.js", documentURL: page, type: "script") == .none)
        // $third-party: blocked from other sites, allowed on the tracker's own site.
        #expect(engine.verdict(url: "https://tracker.example/t.js", documentURL: page, type: "script") == .block)
        #expect(engine.verdict(
            url: "https://tracker.example/t.js", documentURL: "https://www.tracker.example/", type: "script") == .none)
    }

    @Test(.enabled(if: available)) func isSafeToQueryFromManyThreads() async throws {
        let engine = try makeEngine()
        let verdicts = await withTaskGroup(of: BlockerEngine.Verdict.self) { group in
            for index in 0..<200 {
                group.addTask {
                    engine.verdict(
                        url: index.isMultiple(of: 2) ? "https://ads.example/\(index).js" : "https://ok.example/\(index).js",
                        documentURL: "https://site.example/", type: "script")
                }
            }
            return await group.reduce(into: [BlockerEngine.Verdict]()) { $0.append($1) }
        }
        #expect(verdicts.filter { $0 == .block }.count == 100)
        #expect(verdicts.filter { $0 == .none }.count == 100)
    }

    @Test(.enabled(if: available)) func requestFilterAppliesSettings() throws {
        let filter = RequestFilter()
        let ad = ("https://ads.example/x.js", "https://site.example/page", "script")
        #expect(!filter.shouldBlock(url: ad.0, documentURL: ad.1, type: ad.2))  // no engine yet

        filter.use(engine: try makeEngine())
        #expect(filter.shouldBlock(url: ad.0, documentURL: ad.1, type: ad.2))
        #expect(!filter.shouldBlock(url: ad.0, documentURL: "about:blank", type: ad.2))

        filter.use(settings: BlockerSettings(allowlist: ["site.example"]))
        #expect(!filter.shouldBlock(url: ad.0, documentURL: ad.1, type: ad.2))

        filter.use(settings: BlockerSettings(isEnabled: false))
        #expect(!filter.shouldBlock(url: ad.0, documentURL: ad.1, type: ad.2))
    }

    @Test func aMissingBundleIsReported() {
        #expect(throws: BlockerEngine.EngineError.self) {
            try BlockerEngine(bundle: URL(fileURLWithPath: "/nonexistent/ublock-engine.js"), suffixList: "", lists: [])
        }
    }
}
