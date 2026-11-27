import Foundation
import Testing
@testable import MosaicCore

@Suite struct SearchEngineTests {
    @Test func buildsSearchURLWithEncodedQuery() {
        let url = SearchEngine.google.searchURL(for: "swift ui & c++ #1")
        #expect(url?.absoluteString == "https://www.google.com/search?q=swift%20ui%20%26%20c%2B%2B%20%231")
    }

    @Test func everyBuiltInEngineProducesAURL() {
        for engine in SearchEngine.builtIn {
            let url = engine.searchURL(for: "mosaic")
            #expect(url?.scheme == "https", "\(engine.name)")
            #expect(url?.absoluteString.contains("mosaic") == true, "\(engine.name)")
        }
    }

    @Test func builtInIDsAreUnique() {
        #expect(Set(SearchEngine.builtIn.map(\.id)).count == SearchEngine.builtIn.count)
    }

    @Test func nextCyclesThroughAllAndWraps() {
        var engine = SearchEngine.builtIn[0]
        var seen: [SearchEngine] = []
        for _ in SearchEngine.builtIn {
            seen.append(engine)
            engine = engine.next
        }
        #expect(seen == SearchEngine.builtIn)
        #expect(engine == SearchEngine.builtIn[0])
    }

    @Test func lookupByID() {
        #expect(SearchEngine.named("duckduckgo") == .duckDuckGo)
        #expect(SearchEngine.named("altavista") == nil)
    }
}

@Suite struct AddressResolverTests {
    private func resolve(_ input: String) -> String? {
        AddressResolver.resolve(input, searchEngine: .duckDuckGo)?.absoluteString
    }

    @Test(arguments: [
        ("example.com", "https://example.com"),
        ("  example.com  ", "https://example.com"),
        ("example.com/path?q=1#top", "https://example.com/path?q=1#top"),
        ("sub.domain.co.uk", "https://sub.domain.co.uk"),
        ("https://example.com/a b", "https://duckduckgo.com/?q=https%3A%2F%2Fexample.com%2Fa%20b"),
        ("http://example.com", "http://example.com"),
        ("localhost", "http://localhost"),
        ("localhost:3000/app", "http://localhost:3000/app"),
        ("127.0.0.1:8080", "http://127.0.0.1:8080"),
        ("192.168.1.1", "http://192.168.1.1"),
        ("example.com:8443", "https://example.com:8443"),
        ("about:blank", "about:blank"),
        ("file:///tmp/x.html", "file:///tmp/x.html"),
        ("moz-extension://abc/popup.html", "moz-extension://abc/popup.html"),
        ("mosaic://history", "mosaic://history"),
    ])
    func treatsAsAddress(input: String, expected: String) {
        #expect(resolve(input) == expected)
    }

    @Test(arguments: [
        "swift ui browser",
        "what is 3.14",
        "3.14",
        "v1.2",
        "hello",
        "node.js tutorial",
        "user@example.com",
        "foo: bar",
        "example.com:notaport",
        "999.1.1.1",
    ])
    func treatsAsSearch(input: String) {
        let url = resolve(input)
        #expect(url?.hasPrefix("https://duckduckgo.com/?q=") == true, "\(input) -> \(url ?? "nil")")
        #expect(!AddressResolver.isAddress(input))
    }

    @Test func emptyInputResolvesToNothing() {
        #expect(resolve("") == nil)
        #expect(resolve("   \n") == nil)
    }
}
