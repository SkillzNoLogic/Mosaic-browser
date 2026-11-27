import Foundation

/// Decides whether two addresses show the same page, for duplicate-tab detection.
public enum URLIdentity {
    /// A key that is equal for addresses Mosaic treats as the same page, or nil for
    /// addresses that never count as duplicates (blank, about:, data:, Mosaic's own pages).
    ///
    /// Web addresses ignore: http vs https, a leading "www.", letter case in the host, the
    /// default port, the fragment (#…), and a trailing slash. The query is significant.
    public static func key(for address: String) -> String? {
        let trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased()
        else { return nil }
        switch scheme {
        case "http", "https":
            guard var host = components.host?.lowercased(), !host.isEmpty else { return nil }
            if host.hasPrefix("www.") { host.removeFirst(4) }
            var key = host
            if let port = components.port, !(port == 80 || port == 443) { key += ":\(port)" }
            var path = components.percentEncodedPath
            while path.count > 1, path.hasSuffix("/") { path.removeLast() }
            if path.isEmpty { path = "/" }
            key += path
            if let query = components.percentEncodedQuery, !query.isEmpty { key += "?" + query }
            return key
        case "file":
            return "file:" + components.percentEncodedPath
        default:
            return nil
        }
    }

    public static func samePage(_ a: String, _ b: String) -> Bool {
        guard let keyA = key(for: a) else { return false }
        return keyA == key(for: b)
    }
}

public enum DuplicateFinder {
    /// The first candidate showing the same page as `address`, skipping `excluding`.
    public static func firstMatch(
        for address: String, among candidates: [(id: UUID, url: String)], excluding: UUID? = nil
    ) -> UUID? {
        guard let key = URLIdentity.key(for: address) else { return nil }
        return candidates.first { $0.id != excluding && URLIdentity.key(for: $0.url) == key }?.id
    }
}

/// Type-to-filter ranking for the tab search panel.
public enum TabSearch {
    /// How well a tab matches `query`, higher is better; nil when it does not match.
    /// Every word of the query has to appear in the title or the address (case and accents
    /// ignored). An empty query matches everything with score 0.
    public static func score(query: String, title: String, url: String) -> Int? {
        let terms = fold(query).split(whereSeparator: \.isWhitespace).map(String.init)
        guard !terms.isEmpty else { return 0 }
        let title = fold(title)
        let address = fold(strippingScheme(url))
        let host = address.split(separator: "/", maxSplits: 1).first.map(String.init) ?? address
        let words = title.split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
        var total = 0
        for term in terms {
            if title.hasPrefix(term) {
                total += 100
            } else if words.contains(where: { $0.hasPrefix(term) }) {
                total += 60
            } else if host.hasPrefix(term) || host.hasPrefix("www." + term) {
                total += 50
            } else if title.contains(term) {
                total += 40
            } else if address.contains(term) {
                total += 20
            } else {
                return nil
            }
        }
        return total
    }

    /// `items` that match, best first; equal scores keep their original order.
    public static func rank<T>(
        _ items: [T], query: String, title: (T) -> String, url: (T) -> String
    ) -> [T] {
        items.enumerated()
            .compactMap { offset, item in score(query: query, title: title(item), url: url(item)).map { (offset, $0, item) } }
            .sorted { $0.1 != $1.1 ? $0.1 > $1.1 : $0.0 < $1.0 }
            .map(\.2)
    }

    private static func fold(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
    }

    private static func strippingScheme(_ url: String) -> String {
        guard let range = url.range(of: "://") else { return url }
        return String(url[range.upperBound...])
    }
}
