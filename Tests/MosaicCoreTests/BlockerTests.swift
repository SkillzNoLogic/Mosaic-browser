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

/// Cosmetic filtering, filter details and validation, on uBO's real parser.
/// Needs the bundle Scripts/bootstrap.sh builds; skipped when it is absent.
@Suite struct CosmeticFilteringTests {
    static let filters = """
    ! Title: Mosaic cosmetic test list
    ||ads.example^
    ##.ad-banner
    ##div[id^="sponsor-"]
    ###top-ad
    example.com##.promo
    example.com#@#.ad-banner
    news.example.com##.newsletter
    ~shop.example.com,example.com##.sidebar-ad
    example.*##.entity-ad
    #@#.globally-excepted
    ##.globally-excepted
    example.com##.styled:style(opacity: 0.5 !important)
    example.com##.text:has-text(Sponsored)
    example.com##+js(nobab)
    other.org##.promo
    """

    private func makeEngine() throws -> BlockerEngine {
        try BlockerEngine(
            bundle: BlockerEngineTests.bundle,
            suffixList: try String(contentsOf: BlockerEngineTests.suffixList, encoding: .utf8),
            lists: [BlockerEngine.List(name: "test", text: Self.filters)])
    }

    @Test(.enabled(if: BlockerEngineTests.available)) func networkEngineIsUnaffected() throws {
        let engine = try makeEngine()
        #expect(engine.filterCount == 1)
        #expect(engine.verdict(url: "https://ads.example/x.js", documentURL: "https://a.com/", type: "script") == .block)
    }

    @Test(.enabled(if: BlockerEngineTests.available)) func genericStylesheet() throws {
        let engine = try makeEngine()
        let css = engine.genericCosmeticCSS()
        let rules = css.split(separator: "\n").map(String.init)
        #expect(rules.contains(".ad-banner{display:none!important;}"))
        #expect(rules.contains(#"div[id^="sponsor-"]{display:none!important;}"#))
        #expect(rules.contains("#top-ad{display:none!important;}"))
        // Excepted everywhere by #@#, so never part of the generic sheet.
        #expect(!css.contains("globally-excepted"))
        // Site-specific filters are not generic.
        #expect(!css.contains(".promo"))
        #expect(engine.genericCosmeticCSS(excluding: [".ad-banner"]).contains(".ad-banner") == false)
        #expect(engine.cosmeticStats.generic == 4)
        #expect(engine.cosmeticStats.unsupported >= 2)  // :has-text and +js
    }

    @Test(.enabled(if: BlockerEngineTests.available)) func siteSpecificRules() throws {
        let engine = try makeEngine()
        let example = engine.cosmeticRules(forHostname: "www.example.com")
        #expect(example.css.contains(".promo"))
        #expect(example.css.contains(".sidebar-ad"))
        #expect(example.css.contains(".entity-ad"))                // example.* entity filter
        #expect(example.css.contains(".styled\n{opacity: 0.5 !important}"))  // cssable :style()
        #expect(!example.css.contains("Sponsored"))                // procedural: not applied
        #expect(!example.css.contains(".newsletter"))              // other subdomain only
        #expect(example.genericExceptions == [".ad-banner"])      // example.com#@#.ad-banner

        let news = engine.cosmeticRules(forHostname: "news.example.com")
        #expect(news.css.contains(".newsletter"))
        #expect(news.css.contains(".promo"))                       // parent domain applies

        let shop = engine.cosmeticRules(forHostname: "shop.example.com")
        #expect(!shop.css.contains(".sidebar-ad"))                 // ~shop.example.com
        #expect(shop.css.contains(".promo"))

        let unrelated = engine.cosmeticRules(forHostname: "unrelated.net")
        #expect(unrelated.css.isEmpty)
        #expect(unrelated.genericExceptions.isEmpty)
    }

    @Test(.enabled(if: BlockerEngineTests.available)) func styleCacheCombinesGenericAndSpecific() throws {
        let cache = CosmeticStyleCache(engine: try makeEngine())
        let page = cache.stylesheet(forHostname: "example.com", includeGeneric: true)
        #expect(page.contains("#top-ad"))
        #expect(page.contains(".promo"))
        #expect(!page.contains(".ad-banner"))                      // the site's generic exception
        let frame = cache.stylesheet(forHostname: "other.org", includeGeneric: false)
        #expect(frame.contains(".promo"))
        #expect(!frame.contains("#top-ad"))
        #expect(cache.rules(forHostname: "EXAMPLE.com") == cache.rules(forHostname: "example.com"))
    }

    @Test(.enabled(if: BlockerEngineTests.available)) func detailsNameTheFilter() throws {
        let engine = try BlockerEngine(
            bundle: BlockerEngineTests.bundle,
            suffixList: try String(contentsOf: BlockerEngineTests.suffixList, encoding: .utf8),
            lists: [BlockerEngine.List(name: "test", text: BlockerEngineTests.filters)])
        let page = "https://news.example.com/"
        let blocked = engine.details(url: "https://ads.example/x.js", documentURL: page, type: "script")
        #expect(blocked.verdict == .block)
        #expect(blocked.filter == "||ads.example^")
        let allowed = engine.details(url: "https://ads.example/allowed.js", documentURL: page, type: "script")
        #expect(allowed.verdict == .allow)
        #expect(allowed.filter.hasPrefix("@@||ads.example/allowed.js"))
        let image = engine.details(url: "https://cdn.example.net/banner/1.png", documentURL: page, type: "image")
        #expect(image.filter.contains("/banner/"))
        #expect(engine.details(url: "https://ok.example/", documentURL: page, type: "script").filter.isEmpty)
    }

    @Test(.enabled(if: BlockerEngineTests.available)) func validatesUserFilters() throws {
        let engine = try makeEngine()
        let checks = engine.validate(filters: """
        ! my filters
        ||tracker.example^$third-party
        example.com##.cookie-banner

        ##.everywhere > .ad
        ||x.example^$bogus
        example.com##+js(set-constant, x, 1)
        example.com##div:has-text(Ad)
        ##[unclosed
        ||y.example^$removeparam=utm_source
        ||z.example^$redirect=noopjs
        """)
        let byLine = Dictionary(uniqueKeysWithValues: checks.map { ($0.line, $0) })
        #expect(byLine[1]?.kind == .comment)
        #expect(byLine[2]?.kind == .network)
        #expect(byLine[3]?.kind == .cosmetic)
        #expect(byLine[4] == nil)                                 // blank lines are skipped
        #expect(byLine[5]?.kind == .cosmetic)
        #expect(byLine[6]?.kind == .error)
        #expect(byLine[6]?.message.contains("bogus") == true)
        #expect(byLine[7]?.kind == .unsupported)
        #expect(byLine[8]?.kind == .unsupported)
        #expect(byLine[9]?.kind == .error)
        #expect(byLine[10]?.kind == .unsupported)
        #expect(byLine[11]?.kind == .network)
        #expect(byLine[11]?.message.isEmpty == false)             // blocks instead of redirecting
        #expect(checks.filter(\.isEffective).count == 4)
    }

    @Test func userFiltersAreLoadedAfterTheLists() throws {
        let root = try Fixtures.temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let lists = Array(FilterListCatalog.defaults.prefix(1))
        let store = FilterListStore(directory: root, lists: lists)
        try Data("! list".utf8).write(to: store.fileURL(for: lists[0]))
        #expect(store.loadLists().map(\.name) == [lists[0].id])
        try store.saveUserFilters("||mine.example^\nexample.com##.mine")
        #expect(store.loadLists().map(\.name) == [lists[0].id, FilterListCatalog.userFiltersName])
        #expect(store.availableListPaths().last == store.userFiltersURL.path)
        #expect(store.listContaining(filter: "||mine.example^") == "My filters")
        #expect(store.listContaining(filter: "||nothing.example^") == nil)
        try store.saveUserFilters("")
        #expect(store.loadLists().count == 1)
    }

    @Test func catalogueOffersStricterListsWithoutClashes() {
        let ids = FilterListCatalog.all.map(\.id)
        #expect(Set(ids).count == ids.count)
        #expect(FilterListCatalog.optional.contains { $0.id == "fanboy-cookiemonster" && $0.group == "annoyances" })
        #expect(FilterListCatalog.optional.allSatisfy { $0.url.scheme == "https" && !$0.summary.isEmpty })
        let enabled: Set<String> = ["easylist", "adguard-cookies"]
        #expect(FilterListCatalog.lists(enabled: enabled).map(\.id) == ["easylist", "adguard-cookies"])
    }
}
