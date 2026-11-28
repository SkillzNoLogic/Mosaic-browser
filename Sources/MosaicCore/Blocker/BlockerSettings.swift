import Foundation

/// The user's content-blocking choices: the master switch and the sites the
/// blocker is turned off for.
public struct BlockerSettings: Equatable, Sendable {
    /// Blocking is on unless the user turns it off.
    public var isEnabled: Bool
    /// Sites (normalised hostnames) where blocking is off. An entry covers the
    /// host and all of its subdomains.
    public private(set) var allowlist: Set<String>

    public init(isEnabled: Bool = true, allowlist: some Sequence<String> = [String]()) {
        self.isEnabled = isEnabled
        self.allowlist = Set(allowlist.compactMap(Self.normalize))
    }

    /// The form a site is stored in: lower-case hostname without a leading
    /// `www.`. Accepts a hostname or a URL; nil for anything without a host.
    public static func normalize(_ site: String) -> String? {
        let trimmed = site.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !trimmed.isEmpty else { return nil }
        var host = trimmed
        if trimmed.contains("://") {
            guard let parsed = URLComponents(string: trimmed)?.host, !parsed.isEmpty else { return nil }
            host = parsed
        } else if let slash = host.firstIndex(where: { "/?#".contains($0) }) {
            host = String(host[..<slash])
        }
        // "host:8080" → "host"; any other colon ("about:blank") means it is not a host.
        if let colon = host.lastIndex(of: ":"), host[host.index(after: colon)...].allSatisfy(\.isNumber) {
            host = String(host[..<colon])
        }
        if host.hasPrefix("www.") { host.removeFirst(4) }
        guard !host.isEmpty, !host.contains(" "), !host.contains(":") else { return nil }
        return host
    }

    /// The site a page belongs to for allowlisting; nil for pages that are not
    /// on the web (`about:`, `file:`, extension pages), which are never filtered.
    public static func site(ofPage url: String) -> String? {
        guard let components = URLComponents(string: url),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https"
        else { return nil }
        return components.host.flatMap(normalize)
    }

    public func isAllowlisted(site: String) -> Bool {
        if allowlist.contains(site) { return true }
        // An entry also covers subdomains: "example.com" covers "news.example.com".
        var rest = Substring(site)
        while let dot = rest.firstIndex(of: ".") {
            rest = rest[rest.index(after: dot)...]
            if allowlist.contains(String(rest)) { return true }
        }
        return false
    }

    /// Whether requests made by `pageURL` should be put to the engine at all.
    public func filters(pageURL: String) -> Bool {
        guard isEnabled, let site = Self.site(ofPage: pageURL) else { return false }
        return !isAllowlisted(site: site)
    }

    public mutating func setAllowlisted(_ allowlisted: Bool, site: String) {
        guard let normalized = Self.normalize(site) else { return }
        if allowlisted {
            allowlist.insert(normalized)
        } else {
            allowlist.remove(normalized)
        }
    }
}
