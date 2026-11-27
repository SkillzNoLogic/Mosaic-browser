import Foundation

/// Address-bar keywords ("w swift" searches Wikipedia), suggestion endpoints and the
/// parsing of their answers.
public enum SearchShortcuts {
    /// What a keyword turned the input into.
    public struct Match: Equatable, Sendable {
        public let engine: SearchEngine
        public let keyword: String
        public let terms: String
    }

    /// The fixed keyword of each built-in engine ("@google", "@ddg"…).
    public static func keyword(for engine: SearchEngine) -> String? {
        switch engine.id {
        case SearchEngine.google.id: "@google"
        case SearchEngine.duckDuckGo.id: "@ddg"
        case SearchEngine.bing.id: "@bing"
        case SearchEngine.brave.id: "@brave"
        case SearchEngine.startpage.id: "@startpage"
        case SearchEngine.ecosia.id: "@ecosia"
        case SearchEngine.kagi.id: "@kagi"
        default: nil
        }
    }

    /// "keyword terms…" → the engine and the terms, or nil when the first word is no keyword
    /// or nothing follows it.
    public static func match(_ input: String, custom: [CustomSearchEngine]) -> Match? {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let space = text.firstIndex(where: \.isWhitespace) else { return nil }
        let word = text[..<space].lowercased()
        let terms = text[space...].trimmingCharacters(in: .whitespacesAndNewlines)
        guard !word.isEmpty, !terms.isEmpty else { return nil }
        if let engine = custom.first(where: { !$0.keyword.isEmpty && $0.keyword.lowercased() == word }) {
            return Match(engine: engine.searchEngine, keyword: engine.keyword, terms: terms)
        }
        if let engine = SearchEngine.builtIn.first(where: { keyword(for: $0) == word }) {
            return Match(engine: engine, keyword: word, terms: terms)
        }
        return nil
    }

    /// Like `AddressResolver.resolve`, but a keyword search wins first.
    public static func resolve(_ input: String, searchEngine: SearchEngine, custom: [CustomSearchEngine]) -> URL? {
        if let match = match(input, custom: custom) { return match.engine.searchURL(for: match.terms) }
        return AddressResolver.resolve(input, searchEngine: searchEngine)
    }

    /// The OpenSearch suggestion endpoint (`%s` for the terms) of an engine, if it has one.
    public static func suggestTemplate(for engine: SearchEngine, custom: [CustomSearchEngine]) -> String? {
        if let entry = custom.first(where: { $0.id == engine.id }) { return entry.suggestTemplate }
        switch engine.id {
        case SearchEngine.google.id: return "https://suggestqueries.google.com/complete/search?client=firefox&q=%s"
        case SearchEngine.duckDuckGo.id: return "https://duckduckgo.com/ac/?q=%s&type=list"
        case SearchEngine.bing.id: return "https://www.bing.com/osjson.aspx?query=%s&form=OSDJAS"
        case SearchEngine.brave.id: return "https://search.brave.com/api/suggest?q=%s"
        case SearchEngine.startpage.id: return "https://www.startpage.com/osuggestions?q=%s"
        case SearchEngine.ecosia.id: return "https://ac.ecosia.org/autocomplete?q=%s&type=list"
        case SearchEngine.kagi.id: return "https://kagi.com/api/autosuggest?q=%s"
        default: return nil
        }
    }

    public static func suggestURL(template: String, query: String) -> URL? {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        guard let encoded = query.addingPercentEncoding(withAllowedCharacters: allowed) else { return nil }
        return URL(string: template.replacingOccurrences(of: "%s", with: encoded))
    }

    /// OpenSearch suggestions: `["query", ["one", "two", …], …]`.
    public static func parseSuggestions(_ data: Data) -> [String] {
        guard let json = try? JSONSerialization.jsonObject(with: data) else { return [] }
        if let array = json as? [Any], array.count > 1, let list = array[1] as? [Any] {
            return list.compactMap { $0 as? String }
        }
        return []
    }
}

extension SearchEngine {
    private final class Registry: @unchecked Sendable {
        let lock = NSLock()
        var engines: [SearchEngine] = []
    }

    private static let registry = Registry()

    /// Engines added at run time (the user's custom search engines, kept up to date by the
    /// library), so `named(_:)` finds them and one can be the default.
    public static var registered: [SearchEngine] {
        get {
            registry.lock.lock()
            defer { registry.lock.unlock() }
            return registry.engines
        }
        set {
            registry.lock.lock()
            registry.engines = newValue
            registry.lock.unlock()
        }
    }
}
