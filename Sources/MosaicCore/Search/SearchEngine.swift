import Foundation

/// A web search provider the address bar can send queries to.
public struct SearchEngine: Identifiable, Hashable, Codable, Sendable {
    public let id: String
    public let name: String
    /// Search URL with `%s` standing in for the percent-encoded query.
    public let searchTemplate: String
    public let homepage: String

    public init(id: String, name: String, searchTemplate: String, homepage: String) {
        self.id = id
        self.name = name
        self.searchTemplate = searchTemplate
        self.homepage = homepage
    }

    public func searchURL(for query: String) -> URL? {
        guard let encoded = query.addingPercentEncoding(withAllowedCharacters: Self.queryAllowed) else {
            return nil
        }
        return URL(string: searchTemplate.replacingOccurrences(of: "%s", with: encoded))
    }

    /// Characters that may appear unescaped inside a single query-string value.
    private static let queryAllowed: CharacterSet = {
        var set = CharacterSet.alphanumerics
        set.insert(charactersIn: "-._~")
        return set
    }()
}

extension SearchEngine {
    public static let google = SearchEngine(
        id: "google", name: "Google",
        searchTemplate: "https://www.google.com/search?q=%s",
        homepage: "https://www.google.com/")
    public static let duckDuckGo = SearchEngine(
        id: "duckduckgo", name: "DuckDuckGo",
        searchTemplate: "https://duckduckgo.com/?q=%s",
        homepage: "https://duckduckgo.com/")
    public static let bing = SearchEngine(
        id: "bing", name: "Bing",
        searchTemplate: "https://www.bing.com/search?q=%s",
        homepage: "https://www.bing.com/")
    public static let brave = SearchEngine(
        id: "brave", name: "Brave Search",
        searchTemplate: "https://search.brave.com/search?q=%s",
        homepage: "https://search.brave.com/")
    public static let startpage = SearchEngine(
        id: "startpage", name: "Startpage",
        searchTemplate: "https://www.startpage.com/sp/search?query=%s",
        homepage: "https://www.startpage.com/")
    public static let ecosia = SearchEngine(
        id: "ecosia", name: "Ecosia",
        searchTemplate: "https://www.ecosia.org/search?q=%s",
        homepage: "https://www.ecosia.org/")
    public static let kagi = SearchEngine(
        id: "kagi", name: "Kagi",
        searchTemplate: "https://kagi.com/search?q=%s",
        homepage: "https://kagi.com/")

    public static let builtIn: [SearchEngine] = [
        .google, .duckDuckGo, .bing, .brave, .startpage, .ecosia, .kagi,
    ]

    public static let `default` = SearchEngine.google

    public static func named(_ id: String) -> SearchEngine? {
        builtIn.first { $0.id == id }
    }

    /// The engine after `self` in `builtIn`, wrapping around.
    public var next: SearchEngine {
        let all = Self.builtIn
        guard let index = all.firstIndex(of: self) else { return .default }
        return all[(index + 1) % all.count]
    }
}
