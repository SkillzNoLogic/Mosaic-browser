import Foundation
import Testing
@testable import MosaicCore

@Suite struct TrackingParameterTests {
    @Test(arguments: [
        ("https://example.com/a?fbclid=123", "https://example.com/a"),
        ("https://example.com/a?id=7&utm_source=news&utm_medium=email", "https://example.com/a?id=7"),
        ("https://example.com/?gclid=x&q=shoes#reviews", "https://example.com/?q=shoes#reviews"),
        ("https://example.com/?UTM_Campaign=Spring&page=2", "https://example.com/?page=2"),
        ("http://shop.example/?q=a%20b&msclkid=9&sort=asc", "http://shop.example/?q=a%20b&sort=asc"),
        ("https://example.com/?utm_custom_thing=1", "https://example.com/"),
        ("https://example.com/?a=1&&mc_eid=2", "https://example.com/?a=1&"),
    ])
    func stripsTrackingParameters(input: String, expected: String) {
        #expect(TrackingParameters.stripped(input) == expected)
    }

    @Test func leavesCleanAndNonWebAddressesAlone() {
        #expect(TrackingParameters.stripped("https://example.com/search?q=fbclid") == nil)
        #expect(TrackingParameters.stripped("https://example.com/") == nil)
        #expect(TrackingParameters.stripped("https://example.com/#utm_source=x") == nil)
        #expect(TrackingParameters.stripped("about:blank?fbclid=1") == nil)
        #expect(TrackingParameters.stripped("mosaic://history?utm_source=1") == nil)
    }

    @Test func honoursFirefoxsAllowList() {
        #expect(TrackingParameters.stripped("https://www.googleadservices.com/pagead/aclk?gclid=1") == nil)
    }

    @Test func geckoListHasExactNamesOnly() {
        #expect(TrackingParameters.allNames.contains("fbclid"))
        #expect(TrackingParameters.allNames.contains("utm_source"))
        #expect(TrackingParameters.allNames.allSatisfy { !$0.contains("*") && $0 == $0.lowercased() })
        #expect(Set(TrackingParameters.allNames).count == TrackingParameters.allNames.count)
    }
}

@Suite struct NavigationPolicyTests {
    private func settings(httpsOnly: Bool = true, strip: Bool = true, exceptions: [String] = []) -> PrivacySettings {
        var settings = PrivacySettings()
        settings.httpsOnly = httpsOnly
        settings.stripTrackingParameters = strip
        settings.httpsExceptions = exceptions
        return settings
    }

    @Test func upgradesPlainHTTP() {
        #expect(NavigationPolicy.rewrite("http://example.com/a?b=1", settings: settings()) == "https://example.com/a?b=1")
        #expect(NavigationPolicy.rewrite("http://example.com:80/", settings: settings()) == "https://example.com/")
        #expect(NavigationPolicy.rewrite("http://example.com:8080/", settings: settings()) == "https://example.com:8080/")
        #expect(NavigationPolicy.rewrite("https://example.com/", settings: settings()) == nil)
    }

    @Test func upgradesAndStripsTogether() {
        #expect(NavigationPolicy.rewrite("http://example.com/?fbclid=1&x=2", settings: settings())
                == "https://example.com/?x=2")
        #expect(NavigationPolicy.rewrite("http://example.com/?fbclid=1", settings: settings(httpsOnly: false))
                == "http://example.com/")
        #expect(NavigationPolicy.rewrite("http://example.com/?fbclid=1", settings: settings(strip: false))
                == "https://example.com/?fbclid=1")
    }

    @Test func respectsExceptionsAndTheUsersChoice() {
        let policy = settings(exceptions: ["example.com"])
        #expect(NavigationPolicy.rewrite("http://www.example.com/", settings: policy) == nil)
        #expect(NavigationPolicy.rewrite("http://news.example.com/", settings: policy) == nil)
        #expect(NavigationPolicy.rewrite("http://notexample.com/", settings: policy) == "https://notexample.com/")
        #expect(NavigationPolicy.rewrite("http://other.org/", settings: settings(), sessionExceptions: ["other.org"]) == nil)
        #expect(NavigationPolicy.rewrite("http://other.org/", settings: settings(), allowInsecure: true) == nil)
    }

    @Test func leavesLocalAddressesOnHTTP() {
        for url in ["http://localhost:3000/", "http://127.0.0.1/", "http://192.168.1.10/", "http://10.0.0.2/x",
                    "http://172.20.1.1/", "http://printer.local/", "http://router/", "http://[::1]:8080/"] {
            #expect(NavigationPolicy.rewrite(url, settings: settings()) == nil, "\(url)")
        }
        #expect(NavigationPolicy.rewrite("http://172.32.0.1/", settings: settings()) == "https://172.32.0.1/")
        #expect(NavigationPolicy.rewrite("http://8.8.8.8/", settings: settings()) == "https://8.8.8.8/")
    }

    @Test func exceptionsAreNormalisedAndSorted() {
        var settings = PrivacySettings()
        settings.setHTTPException(true, site: "https://WWW.Zeta.com/page")
        settings.setHTTPException(true, site: "alpha.org")
        settings.setHTTPException(true, site: "alpha.org")
        #expect(settings.httpsExceptions == ["alpha.org", "zeta.com"])
        settings.setHTTPException(false, site: "zeta.com")
        #expect(settings.httpsExceptions == ["alpha.org"])
    }

    @Test func settingsDecodeFromOlderVersions() throws {
        let decoded = try JSONDecoder().decode(PrivacySettings.self, from: Data(#"{"httpsOnly":true}"#.utf8))
        #expect(decoded.httpsOnly)
        #expect(decoded.stripTrackingParameters)
        #expect(decoded.blockThirdPartyCookies)
        #expect(decoded.dnsOverHTTPS.mode == .off)
        let roundTrip = try JSONDecoder().decode(PrivacySettings.self, from: JSONEncoder().encode(decoded))
        #expect(roundTrip == decoded)
    }
}

@Suite struct DNSOverHTTPSTests {
    @Test func offMeansOffInBothEngines() {
        let setting = DNSOverHTTPS.Setting()
        #expect(setting.template == nil)
        #expect(setting.geckoPreferences["network.trr.mode"] as? Int == 5)
        #expect(setting.chromiumPreferences["dns_over_https.mode"] as? String == "off")
    }

    @Test func providerAndStrictness() {
        let automatic = DNSOverHTTPS.Setting(mode: .automatic, providerID: "quad9")
        #expect(automatic.template == "https://dns.quad9.net/dns-query")
        #expect(automatic.geckoPreferences["network.trr.mode"] as? Int == 2)
        #expect(automatic.chromiumPreferences["dns_over_https.mode"] as? String == "automatic")
        let strict = DNSOverHTTPS.Setting(mode: .strict, providerID: "cloudflare")
        #expect(strict.geckoPreferences["network.trr.mode"] as? Int == 3)
        #expect(strict.geckoPreferences["network.trr.uri"] as? String == "https://cloudflare-dns.com/dns-query")
        #expect(strict.chromiumPreferences["dns_over_https.mode"] as? String == "secure")
    }

    @Test func customTemplatesAreValidated() {
        #expect(DNSOverHTTPS.isValidTemplate("https://dns.example/dns-query{?dns}"))
        #expect(DNSOverHTTPS.isValidTemplate(" https://dns.example/q "))
        #expect(!DNSOverHTTPS.isValidTemplate("http://dns.example/dns-query"))
        #expect(!DNSOverHTTPS.isValidTemplate("dns.example"))
        #expect(!DNSOverHTTPS.isValidTemplate(""))
        #expect(DNSOverHTTPS.Setting(mode: .strict, providerID: "custom", customTemplate: "ftp://x").template == nil)
        #expect(DNSOverHTTPS.Setting(mode: .strict, providerID: "custom", customTemplate: "https://x.example/q").template
                == "https://x.example/q")
    }
}

@Suite struct SiteDataTests {
    static let psl = PublicSuffixList(text: """
    // comment
    com
    uk
    co.uk
    *.ck
    !www.ck
    github.io
    """)

    @Test(arguments: [
        ("www.example.com", "example.com"),
        (".example.com", "example.com"),
        ("a.b.example.co.uk", "example.co.uk"),
        ("user.github.io", "user.github.io"),
        ("foo.bar.ck", "foo.bar.ck"),
        ("www.ck", "www.ck"),
        ("a.www.ck", "www.ck"),
        ("localhost", "localhost"),
        ("192.168.1.1", "192.168.1.1"),
        ("example.unknowntld", "example.unknowntld"),
    ])
    func groupsHostsIntoSites(host: String, site: String) {
        #expect(Self.psl.site(of: host) == site)
    }

    @Test func mergesBothEnginesPerSite() {
        let entries = SiteDataSummary.merge([
            .init(engine: .chromium, host: ".example.com", cookies: 1),
            .init(engine: .chromium, host: "www.example.com", cookies: 1),
            .init(engine: .gecko, host: "shop.example.com", cookies: 2, storageBytes: 4096),
            .init(engine: .gecko, host: "other.co.uk", cookies: 0, storageBytes: 10),
            .init(engine: .chromium, host: "", cookies: 1),
        ], suffixList: Self.psl)
        #expect(entries.map(\.site) == ["example.com", "other.co.uk"])
        let example = entries[0]
        #expect(example.chromiumCookies == 2)
        #expect(example.geckoCookies == 2)
        #expect(example.geckoStorageBytes == 4096)
        #expect(example.cookieCount == 4)
        #expect(example.hosts == [".example.com", "www.example.com", "shop.example.com"])
        #expect(example.chromiumOrigins.contains("https://shop.example.com"))
        #expect(example.chromiumOrigins.contains("http://example.com"))
        #expect(!example.chromiumOrigins.contains { $0.contains("//.") })
    }
}

@MainActor
@Suite struct BrowsingDataRegistryTests {
    final class FakeStore: BrowsingDataClearing {
        let browsingDataName: String
        let clearableBrowsingData: Set<BrowsingDataKind>
        var calls: [(Set<BrowsingDataKind>, Date?)] = []

        init(_ name: String, _ kinds: Set<BrowsingDataKind>) {
            browsingDataName = name
            clearableBrowsingData = kinds
        }

        func clearBrowsingData(_ kinds: Set<BrowsingDataKind>, since: Date?) async {
            calls.append((kinds, since))
        }
    }

    @Test func asksOnlyServicesHoldingTheKinds() async {
        let registry = BrowsingDataRegistry()
        let history = FakeStore("History", [.history])
        let downloads = FakeStore("Downloads", [.downloads])
        registry.register(history)
        registry.register(downloads)
        registry.register(history)
        #expect(registry.clearers.count == 2)
        #expect(registry.coveredKinds == [.history, .downloads])

        let since = Date(timeIntervalSince1970: 1000)
        let asked = await registry.clear([.history, .cache], since: since)
        #expect(asked == ["History"])
        #expect(history.calls.count == 1)
        #expect(history.calls.first?.0 == [.history])
        #expect(history.calls.first?.1 == since)
        #expect(downloads.calls.isEmpty)

        registry.unregister(history)
        #expect(registry.clearers.count == 1)
    }

    @Test func holdsServicesWeakly() {
        let registry = BrowsingDataRegistry()
        do {
            let temporary = FakeStore("Temporary", [.history])
            registry.register(temporary)
            #expect(registry.clearers.count == 1)
        }
        #expect(registry.clearers.isEmpty)
    }

    @Test func timeRanges() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        #expect(BrowsingDataTimeRange.allTime.startDate(now: now) == nil)
        #expect(BrowsingDataTimeRange.lastHour.startDate(now: now) == now.addingTimeInterval(-3600))
        #expect(BrowsingDataTimeRange.last4Weeks.startDate(now: now) == now.addingTimeInterval(-28 * 86_400))
    }
}

@Suite struct BlockLogTests {
    @Test func recordsPerSiteWithCounts() {
        let log = BlockLog(maxPages: 2, maxEntries: 2)
        // Chromium reports the page's site, not the page.
        log.record(url: "https://ads.example/a.js", documentURL: "https://a.com/", type: "script")
        log.record(url: "https://ads.example/a.js", documentURL: "https://a.com/", type: "script")
        log.record(url: "https://ads.example/b.gif", documentURL: "https://a.com/", type: "image")
        log.record(url: "https://ads.example/c.gif", documentURL: "https://a.com/", type: "image")  // over the cap
        #expect(log.entries(forPage: "https://news.a.com/story?id=1").map(\.count) == [2, 1])
        #expect(log.entries(forPage: "https://a.com/").first?.documentURL == "https://a.com/")
        #expect(log.entries(forPage: "https://nota.com/").isEmpty)

        log.record(url: "x", documentURL: "https://b.com/", type: "script")
        log.record(url: "y", documentURL: "https://c.com/", type: "script")  // evicts a.com
        #expect(log.entries(forPage: "https://a.com/").isEmpty)
        log.clear(page: "https://www.b.com/page")
        #expect(log.entries(forPage: "https://b.com/").isEmpty)
        #expect(log.entries(forPage: "https://c.com/x").count == 1)
    }

    @Test func injectionScriptEmbedsCSSSafely() throws {
        #expect(CosmeticInjection.script(css: "") == nil)
        let script = try #require(CosmeticInjection.script(css: ".ad[title=\"x\"]\n{display:none!important;}</script>"))
        #expect(script.contains("adoptedStyleSheets"))
        #expect(script.contains(#".ad[title=\"x\"]\n{display:none!important;}<\/script>"#))
    }
}
