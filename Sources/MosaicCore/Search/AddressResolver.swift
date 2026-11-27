import Foundation

/// Turns whatever was typed into the address bar into a URL: either the address
/// itself, or a search on the current search engine.
public enum AddressResolver {
    /// Schemes that are passed through untouched when typed explicitly.
    private static let passthroughSchemes: Set<String> = [
        "http", "https", "file", "about", "data", "blob", "view-source",
        "chrome", "chrome-extension", "moz-extension", "ftp", "mailto",
        "mosaic",  // Mosaic's own pages (bookmarks, history, downloads…)
    ]

    public static func resolve(_ input: String, searchEngine: SearchEngine) -> URL? {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }

        if let url = explicitURL(text) { return url }
        if let url = hostLikeURL(text) { return url }
        return searchEngine.searchURL(for: text)
    }

    /// True when the input would be treated as an address rather than a search.
    public static func isAddress(_ input: String) -> Bool {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        return explicitURL(text) != nil || hostLikeURL(text) != nil
    }

    private static func explicitURL(_ text: String) -> URL? {
        guard let colon = text.firstIndex(of: ":") else { return nil }
        let scheme = text[..<colon].lowercased()
        guard passthroughSchemes.contains(scheme) else { return nil }
        // "about:blank" and "data:..." legitimately contain spaces rarely; anything
        // hierarchical with whitespace is a search ("https is great: why").
        let rest = text[text.index(after: colon)...]
        if rest.hasPrefix("//"), text.contains(where: \.isWhitespace) { return nil }
        if rest.isEmpty { return nil }
        return URL(string: text) ?? URL(string: text.addingPercentEncoding(
            withAllowedCharacters: .urlFragmentAllowed) ?? text)
    }

    private static func hostLikeURL(_ text: String) -> URL? {
        guard !text.contains(where: \.isWhitespace) else { return nil }

        // Split "host[:port]" from "/path?query#fragment".
        let authorityEnd = text.firstIndex(where: { "/?#".contains($0) }) ?? text.endIndex
        let authority = String(text[..<authorityEnd])
        guard !authority.isEmpty, !authority.contains("@") else { return nil }

        var host = authority
        if let colon = authority.lastIndex(of: ":") {
            let port = authority[authority.index(after: colon)...]
            guard !port.isEmpty, port.allSatisfy(\.isNumber), UInt16(port) != nil else { return nil }
            host = String(authority[..<colon])
        }
        guard !host.isEmpty else { return nil }

        let lowered = host.lowercased()
        let isLocal = lowered == "localhost" || lowered.hasSuffix(".localhost") || isIPv4(lowered)
        guard isLocal || looksLikeDomain(lowered) else { return nil }

        return URL(string: (isLocal ? "http://" : "https://") + text)
    }

    private static func isIPv4(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        return parts.count == 4 && parts.allSatisfy { part in
            !part.isEmpty && part.count <= 3 && part.allSatisfy(\.isNumber) && Int(part)! <= 255
        }
    }

    private static func looksLikeDomain(_ host: String) -> Bool {
        let labels = host.split(separator: ".", omittingEmptySubsequences: false)
        guard labels.count >= 2, let tld = labels.last else { return false }
        // A TLD is letters only (or punycode); "3.14" and "v1.2" are searches.
        let tldOK = tld.count >= 2 && (tld.allSatisfy(\.isLetter) || tld.hasPrefix("xn--"))
        let labelsOK = labels.allSatisfy { label in
            !label.isEmpty && label.count <= 63
                && label.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" }
                && !label.hasPrefix("-") && !label.hasSuffix("-")
        }
        return tldOK && labelsOK
    }
}
