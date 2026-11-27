import Foundation

/// The user's privacy and network-security choices (Settings › Privacy & Security),
/// apart from the content blocker's own (`BlockerSettings`).
public struct PrivacySettings: Codable, Equatable, Sendable {
    /// Load every page over HTTPS; plain-HTTP pages only after the user agrees.
    public var httpsOnly = false
    /// Sites (normalised hostnames, subdomains included) that may load over HTTP.
    public var httpsExceptions: [String] = []
    /// Remove tracking parameters (`fbclid`, `utm_source`, …) from addresses.
    public var stripTrackingParameters = true
    /// Refuse cookies and storage for third-party frames and requests.
    public var blockThirdPartyCookies = true
    /// Delete cookies and site data when Mosaic quits.
    public var clearCookiesOnExit = false
    public var dnsOverHTTPS = DNSOverHTTPS.Setting()

    public init() {}

    // Tolerant decoding: settings saved by an older Mosaic lack newer keys.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = PrivacySettings()
        httpsOnly = try container.decodeIfPresent(Bool.self, forKey: .httpsOnly) ?? defaults.httpsOnly
        httpsExceptions = try container.decodeIfPresent([String].self, forKey: .httpsExceptions) ?? []
        stripTrackingParameters = try container.decodeIfPresent(Bool.self, forKey: .stripTrackingParameters)
            ?? defaults.stripTrackingParameters
        blockThirdPartyCookies = try container.decodeIfPresent(Bool.self, forKey: .blockThirdPartyCookies)
            ?? defaults.blockThirdPartyCookies
        clearCookiesOnExit = try container.decodeIfPresent(Bool.self, forKey: .clearCookiesOnExit) ?? false
        dnsOverHTTPS = try container.decodeIfPresent(DNSOverHTTPS.Setting.self, forKey: .dnsOverHTTPS) ?? .init()
    }

    /// Whether `host` (or a parent domain of it) is in `httpsExceptions`.
    public func allowsHTTP(host: String) -> Bool {
        guard let site = BlockerSettings.normalize(host) else { return false }
        return httpsExceptions.contains { site == $0 || site.hasSuffix("." + $0) }
    }

    public mutating func setHTTPException(_ allowed: Bool, site: String) {
        guard let normalized = BlockerSettings.normalize(site) else { return }
        httpsExceptions.removeAll { $0 == normalized }
        if allowed { httpsExceptions.append(normalized) }
        httpsExceptions.sort()
    }
}

/// What to do with a top-level navigation before it is loaded: the HTTPS-only
/// upgrade and tracking-parameter removal, shared by both engines' code paths.
public enum NavigationPolicy {
    /// The address to load instead of `url`, or nil to load it as it is.
    /// - Parameters:
    ///   - sessionExceptions: hosts the user let through over HTTP this session.
    ///   - allowInsecure: the user chose "Continue to HTTP Site" for this load.
    public static func rewrite(
        _ url: String, settings: PrivacySettings, sessionExceptions: Set<String> = [], allowInsecure: Bool = false
    ) -> String? {
        var result = url
        if settings.stripTrackingParameters, let stripped = TrackingParameters.stripped(result) {
            result = stripped
        }
        if settings.httpsOnly, !allowInsecure, let upgraded = httpsUpgrade(result, settings: settings,
                                                                            sessionExceptions: sessionExceptions) {
            result = upgraded
        }
        return result == url ? nil : result
    }

    /// `https://` for an `http://` address, unless the host is exempt. Local
    /// addresses (localhost, `.local`, private IPv4 ranges, single-label names) stay
    /// on HTTP: they rarely have certificates, and their traffic does not leave the
    /// machine or network (Firefox exempts them the same way).
    public static func httpsUpgrade(
        _ url: String, settings: PrivacySettings, sessionExceptions: Set<String> = []
    ) -> String? {
        guard url.lowercased().hasPrefix("http://"), var components = URLComponents(string: url),
              let host = components.host?.lowercased(), !host.isEmpty
        else { return nil }
        if isLocal(host: host) || settings.allowsHTTP(host: host) { return nil }
        if let site = BlockerSettings.normalize(host),
           sessionExceptions.contains(where: { site == $0 || site.hasSuffix("." + $0) }) {
            return nil
        }
        components.scheme = "https"
        if components.port == 80 { components.port = nil }
        return components.string
    }

    public static func isLocal(host: String) -> Bool {
        let host = host.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        if host == "localhost" || host.hasSuffix(".localhost") || host.hasSuffix(".local")
            || host.hasSuffix(".internal") || host.hasSuffix(".home.arpa") || !host.contains(".") {
            return true
        }
        if host == "::1" { return true }
        let octets = host.split(separator: ".").compactMap { Int($0) }
        guard octets.count == 4, host.allSatisfy({ $0.isNumber || $0 == "." }) else { return false }
        switch (octets[0], octets[1]) {
        case (10, _), (127, _), (192, 168), (169, 254): return true
        case (172, 16...31): return true
        default: return false
        }
    }
}
