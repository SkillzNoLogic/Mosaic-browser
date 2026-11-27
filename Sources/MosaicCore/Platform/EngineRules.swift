import Foundation

/// "Always open example.com in Chromium": a site → engine rule.
///
/// The engine is stored by name ("gecko", "chromium") so this type stays independent
/// of the app's engine enumeration.
public struct EngineRule: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    /// Normalised domain: lowercase, no scheme, path, port or leading "www.".
    public var domain: String
    public var engine: String
    /// Also match subdomains (mail.example.com for example.com).
    public var includesSubdomains: Bool

    public init(id: UUID = UUID(), domain: String, engine: String, includesSubdomains: Bool = true) {
        self.id = id
        self.domain = domain
        self.engine = engine
        self.includesSubdomains = includesSubdomains
    }

    /// Whether this rule applies to `host` (already normalised).
    public func matches(host: String) -> Bool {
        host == domain || (includesSubdomains && host.hasSuffix("." + domain))
    }
}

/// An ordered set of engine rules with the matching policy.
///
/// Policy: rules apply to http and https pages only. When several rules match, the most
/// specific domain wins (mail.example.com beats example.com); for the same domain, a rule
/// that does not include subdomains (an exact rule) wins. One rule per domain: adding a
/// rule for a domain that already has one replaces it.
public struct EngineRuleSet: Codable, Equatable, Sendable {
    public private(set) var rules: [EngineRule]

    public init(rules: [EngineRule] = []) {
        self.rules = []
        for rule in rules { upsert(rule) }
    }

    /// The rule deciding `url`'s engine, if any.
    public func rule(for url: URL) -> EngineRule? {
        guard let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = url.host.flatMap(Self.normalizedHost)
        else { return nil }
        return rules
            .filter { $0.matches(host: host) }
            .max { lhs, rhs in
                if lhs.domain.count != rhs.domain.count { return lhs.domain.count < rhs.domain.count }
                return lhs.includesSubdomains && !rhs.includesSubdomains
            }
    }

    public func engine(for url: URL) -> String? { rule(for: url)?.engine }

    public func engine(forAddress address: String) -> String? {
        URL(string: address).flatMap(engine(for:))
    }

    /// Adds `rule`, replacing any rule for the same domain. Returns false if the domain is invalid.
    @discardableResult
    public mutating func upsert(_ rule: EngineRule) -> Bool {
        guard let domain = Self.normalizedDomain(rule.domain) else { return false }
        var rule = rule
        rule.domain = domain
        if let index = rules.firstIndex(where: { $0.id == rule.id }) ?? rules.firstIndex(where: { $0.domain == domain }) {
            rules[index] = rule
            // Editing a rule onto another rule's domain must not leave two for one domain.
            rules.removeAll { $0.domain == domain && $0.id != rule.id }
        } else {
            rules.append(rule)
        }
        rules.sort { $0.domain < $1.domain }
        return true
    }

    public mutating func remove(id: UUID) {
        rules.removeAll { $0.id == id }
    }

    // MARK: - Normalisation

    /// Turns what someone typed ("https://www.Example.com/path", "example.com:8080",
    /// "*.example.com") into the domain a rule stores, or nil if it is not a domain.
    public static func normalizedDomain(_ input: String) -> String? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if text.hasPrefix("*.") { text.removeFirst(2) }
        if text.contains("://"), let host = URL(string: text)?.host {
            text = host
        } else {
            // Strip a path, query or port typed without a scheme.
            if let cut = text.firstIndex(where: { "/?#".contains($0) }) { text = String(text[..<cut]) }
            if let colon = text.lastIndex(of: ":"), !text.contains("]") { text = String(text[..<colon]) }
        }
        return normalizedHost(text)
    }

    /// Lowercase, without a trailing dot or leading "www."; nil if it is not a hostname.
    public static func normalizedHost(_ host: String) -> String? {
        var host = host.lowercased()
        if host.hasSuffix(".") { host.removeLast() }
        if host.hasPrefix("www.") { host.removeFirst(4) }
        guard !host.isEmpty, host.count <= 253 else { return nil }
        let labels = host.split(separator: ".", omittingEmptySubsequences: false)
        let allowed = Set("abcdefghijklmnopqrstuvwxyz0123456789-")
        for label in labels {
            guard !label.isEmpty, label.count <= 63, label.allSatisfy(allowed.contains),
                  label.first != "-", label.last != "-"
            else { return nil }
        }
        return host
    }
}
