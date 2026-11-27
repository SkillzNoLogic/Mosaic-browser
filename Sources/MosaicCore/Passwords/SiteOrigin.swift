import Foundation

/// A web origin (scheme + host + port), the unit passwords are saved and filled by.
///
/// Mosaic only fills a password into a page whose origin is *exactly* the one the password
/// was saved for: `https://example.com` never fills `http://example.com`,
/// `https://www.example.com` or `https://example.com:8443`. This is stricter than some
/// browsers (which also offer subdomains), on purpose: a subdomain can be run by someone else.
public struct SiteOrigin: Hashable, Codable, Sendable, Comparable, CustomStringConvertible {
    /// "http" or "https".
    public let scheme: String
    /// Lowercased host name, IPv4 address or IPv6 address (without brackets).
    public let host: String
    /// The port, or nil for the scheme's default (80 for http, 443 for https).
    public let port: Int?

    public init?(scheme: String, host: String, port: Int?) {
        let scheme = scheme.lowercased()
        var host = host.lowercased()
        if host.hasPrefix("["), host.hasSuffix("]") { host = String(host.dropFirst().dropLast()) }
        if host.hasSuffix(".") { host.removeLast() }
        guard scheme == "http" || scheme == "https", !host.isEmpty,
              !host.contains(where: { $0.isWhitespace || "/?#@\\".contains($0) })
        else { return nil }
        if let port, !(1...65535).contains(port) { return nil }
        self.scheme = scheme
        self.host = host
        self.port = (port == Self.defaultPort(for: scheme)) ? nil : port
    }

    /// The origin of a page address. Only http and https pages have one here.
    public init?(url: String) {
        let text = url.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let components = URLComponents(string: text), let scheme = components.scheme,
              let host = components.host
        else { return nil }
        self.init(scheme: scheme, host: host, port: components.port)
    }

    /// What a person types in the "Website" field: "example.com", "https://example.com/login",
    /// "localhost:8080". Without a scheme, https is assumed.
    public init?(userInput: String) {
        let text = userInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        if text.contains("://") {
            self.init(url: text)
        } else {
            self.init(url: "https://" + text)
        }
    }

    /// The serialised origin, as browsers write it: `https://example.com`, `http://127.0.0.1:8000`.
    public var string: String {
        let printedHost = host.contains(":") ? "[\(host)]" : host
        return port.map { "\(scheme)://\(printedHost):\($0)" } ?? "\(scheme)://\(printedHost)"
    }

    public var description: String { string }

    /// Short form for lists and prompts: the host, plus the port when it is not the default.
    /// The scheme is left out for https and shown for http, so an unencrypted site stands out.
    public var displayName: String {
        let printedHost = host.contains(":") ? "[\(host)]" : host
        let hostPort = port.map { "\(printedHost):\($0)" } ?? printedHost
        return scheme == "https" ? hostPort : "http://\(hostPort)"
    }

    /// The port actually used, defaults included.
    public var effectivePort: Int { port ?? Self.defaultPort(for: scheme) }

    public static func defaultPort(for scheme: String) -> Int {
        scheme == "http" ? 80 : 443
    }

    public static func < (lhs: SiteOrigin, rhs: SiteOrigin) -> Bool {
        (lhs.host, lhs.scheme, lhs.effectivePort) < (rhs.host, rhs.scheme, rhs.effectivePort)
    }

    // Codable as the serialised string, so stored lists stay readable.
    public init(from decoder: Decoder) throws {
        let text = try decoder.singleValueContainer().decode(String.self)
        guard let origin = SiteOrigin(url: text) else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "not an origin: \(text)"))
        }
        self = origin
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(string)
    }
}
