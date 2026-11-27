import Foundation
import Testing
@testable import MosaicCore

@Suite struct EngineRulesTests {
    private func url(_ text: String) -> URL { URL(string: text)! }

    @Test func normalisesWhatPeopleType() {
        #expect(EngineRuleSet.normalizedDomain("https://www.Example.com/path?q=1") == "example.com")
        #expect(EngineRuleSet.normalizedDomain("example.com:8080") == "example.com")
        #expect(EngineRuleSet.normalizedDomain("  *.Example.COM ") == "example.com")
        #expect(EngineRuleSet.normalizedDomain("mail.example.com/") == "mail.example.com")
        #expect(EngineRuleSet.normalizedDomain("localhost") == "localhost")
        #expect(EngineRuleSet.normalizedDomain("127.0.0.1") == "127.0.0.1")
        #expect(EngineRuleSet.normalizedDomain("example.com.") == "example.com")
    }

    @Test func rejectsNonDomains() {
        #expect(EngineRuleSet.normalizedDomain("") == nil)
        #expect(EngineRuleSet.normalizedDomain("not a domain") == nil)
        #expect(EngineRuleSet.normalizedDomain("-bad.com") == nil)
        #expect(EngineRuleSet.normalizedDomain("a..b") == nil)
        #expect(EngineRuleSet.normalizedDomain("exa_mple.com") == nil)
    }

    @Test func matchesDomainAndSubdomains() {
        let set = EngineRuleSet(rules: [EngineRule(domain: "example.com", engine: "chromium")])
        #expect(set.engine(for: url("https://example.com/")) == "chromium")
        #expect(set.engine(for: url("https://www.example.com/a")) == "chromium")
        #expect(set.engine(for: url("http://mail.example.com")) == "chromium")
        #expect(set.engine(for: url("https://a.b.example.com:8443/x")) == "chromium")
        #expect(set.engine(for: url("https://notexample.com")) == nil)
        #expect(set.engine(for: url("https://example.com.evil.net")) == nil)
    }

    @Test func exactRulesIgnoreSubdomains() {
        let set = EngineRuleSet(rules: [EngineRule(domain: "example.com", engine: "chromium", includesSubdomains: false)])
        #expect(set.engine(for: url("https://example.com")) == "chromium")
        #expect(set.engine(for: url("https://www.example.com")) == "chromium")  // www. is the same site
        #expect(set.engine(for: url("https://mail.example.com")) == nil)
    }

    @Test func mostSpecificRuleWins() {
        let set = EngineRuleSet(rules: [
            EngineRule(domain: "google.com", engine: "chromium"),
            EngineRule(domain: "mail.google.com", engine: "gecko"),
        ])
        #expect(set.engine(for: url("https://mail.google.com/inbox")) == "gecko")
        #expect(set.engine(for: url("https://docs.google.com")) == "chromium")
    }

    @Test func onlyWebPages() {
        let set = EngineRuleSet(rules: [EngineRule(domain: "example.com", engine: "chromium")])
        #expect(set.engine(for: url("file:///example.com/index.html")) == nil)
        #expect(set.engine(forAddress: "mosaic://history") == nil)
        #expect(set.engine(forAddress: "about:blank") == nil)
        #expect(set.engine(forAddress: "https://EXAMPLE.com") == "chromium")
    }

    @Test func oneRulePerDomain() {
        var set = EngineRuleSet()
        let r1 = set.upsert(EngineRule(domain: "https://www.example.com", engine: "chromium"))
        #expect(r1)
        let r2 = set.upsert(EngineRule(domain: "example.com", engine: "gecko"))
        #expect(r2)
        #expect(set.rules.count == 1)
        #expect(set.rules[0].engine == "gecko")
        let r3 = set.upsert(EngineRule(domain: "nope nope", engine: "gecko"))
        #expect(!r3)

        // Editing an existing rule onto another rule's domain merges them.
        let other = EngineRule(domain: "other.org", engine: "chromium")
        set.upsert(other)
        var edited = other
        edited.domain = "example.com"
        set.upsert(edited)
        #expect(set.rules.count == 1)
        #expect(set.rules[0].id == other.id)

        set.remove(id: other.id)
        #expect(set.rules.isEmpty)
    }

    @Test func roundTripsThroughJSON() throws {
        let set = EngineRuleSet(rules: [
            EngineRule(domain: "b.com", engine: "gecko"), EngineRule(domain: "a.com", engine: "chromium"),
        ])
        let decoded = try JSONDecoder().decode(EngineRuleSet.self, from: JSONEncoder().encode(set))
        #expect(decoded == set)
        #expect(decoded.rules.map(\.domain) == ["a.com", "b.com"])
    }
}

@Suite struct LanguagePreferencesTests {
    @Test func normalisesTags() {
        #expect(LanguagePreferences.normalize("EN_us") == "en-US")
        #expect(LanguagePreferences.normalize("zh-hant-tw") == "zh-Hant-TW")
        #expect(LanguagePreferences.normalize("es-419") == "es-419")
        #expect(LanguagePreferences.normalize("fil") == "fil")
        #expect(LanguagePreferences.normalize("e") == nil)
        #expect(LanguagePreferences.normalize("english") == nil)
        #expect(LanguagePreferences.normalize("en-") == nil)
        #expect(LanguagePreferences.normalize("en,fr") == nil)
    }

    @Test func systemDefaultsAddBaseLanguages() {
        let prefs = LanguagePreferences.fromSystem(["en-AU", "fr-FR", "en-GB"])
        #expect(prefs.tags == ["en-AU", "en", "fr-FR", "fr", "en-GB"])
        #expect(LanguagePreferences.fromSystem([]).tags == ["en-US", "en"])
    }

    @Test func editing() {
        var prefs = LanguagePreferences(tags: ["en-GB", "en"])
        let r4 = prefs.add("fr")
        #expect(r4)
        let r5 = prefs.add("FR")
        #expect(!r5)         // already listed
        let r6 = prefs.add("nonsense!")
        #expect(!r6)
        prefs.move("fr", by: -2)
        #expect(prefs.tags == ["fr", "en-GB", "en"])
        prefs.move("fr", by: -1)          // already first
        #expect(prefs.tags.first == "fr")
        prefs.move(fromOffsets: IndexSet(integer: 0), toOffset: 3)
        #expect(prefs.tags == ["en-GB", "en", "fr"])
        let r7 = prefs.remove("en")
        #expect(r7)
        let r8 = prefs.remove("fr")
        #expect(r8)
        let r9 = prefs.remove("en-GB")
        #expect(!r9)   // the last one stays
        #expect(prefs.tags == ["en-GB"])
    }

    @Test func formatsForEngines() {
        let prefs = LanguagePreferences(tags: ["de-CH", "de", "en"])
        #expect(prefs.preferenceValue == "de-CH,de,en")
        #expect(prefs.acceptLanguageHeader == "de-CH,de;q=0.9,en;q=0.8")
        #expect(LanguagePreferences.displayName(for: "en-GB") == "English (United Kingdom)")
    }

    @Test func commonListIsValid() {
        for tag in LanguagePreferences.common {
            #expect(LanguagePreferences.normalize(tag) == tag)
        }
    }
}
