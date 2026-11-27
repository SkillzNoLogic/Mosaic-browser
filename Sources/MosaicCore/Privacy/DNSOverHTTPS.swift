import Foundation

/// DNS over HTTPS: resolving names through an encrypted resolver instead of the
/// network's.
public enum DNSOverHTTPS {
    public struct Provider: Hashable, Identifiable, Sendable {
        public let id: String
        public let name: String
        /// RFC 8484 URI template, as both engines take it.
        public let template: String
        public let note: String
    }

    /// Well-known public resolvers with no-logging policies.
    public static let providers: [Provider] = [
        Provider(id: "cloudflare", name: "Cloudflare", template: "https://cloudflare-dns.com/dns-query",
                 note: "1.1.1.1. Fast; keeps minimal logs for 24 hours."),
        Provider(id: "quad9", name: "Quad9", template: "https://dns.quad9.net/dns-query",
                 note: "Swiss non-profit. Blocks known malicious domains."),
        Provider(id: "mullvad", name: "Mullvad", template: "https://dns.mullvad.net/dns-query",
                 note: "No logging. No filtering."),
        Provider(id: "nextdns", name: "NextDNS", template: "https://dns.nextdns.io",
                 note: "Configurable filtering with an account."),
        Provider(id: "google", name: "Google Public DNS", template: "https://dns.google/dns-query",
                 note: "Large and fast; logs are kept for a short time."),
    ]

    public enum Mode: String, Codable, CaseIterable, Sendable {
        /// The system's resolver.
        case off
        /// Try the encrypted resolver, fall back to the system's when it fails.
        case automatic
        /// Only the encrypted resolver; names fail to resolve without it.
        case strict
    }

    public struct Setting: Codable, Equatable, Sendable {
        public var mode: Mode = .off
        /// A provider's `id`, or "custom".
        public var providerID: String = "cloudflare"
        public var customTemplate: String = ""

        public init(mode: Mode = .off, providerID: String = "cloudflare", customTemplate: String = "") {
            self.mode = mode
            self.providerID = providerID
            self.customTemplate = customTemplate
        }

        /// The template in effect; nil when off, or when a custom one is not valid.
        public var template: String? {
            guard mode != .off else { return nil }
            if providerID == "custom" {
                return DNSOverHTTPS.isValidTemplate(customTemplate) ? customTemplate.trimmed : nil
            }
            return DNSOverHTTPS.providers.first { $0.id == providerID }?.template
        }

        /// Gecko's `network.trr.*` preferences. TRR mode 2 = encrypted first, 3 =
        /// encrypted only, 5 = off by choice.
        public var geckoPreferences: [String: Any] {
            guard let template else { return ["network.trr.mode": 5] }
            return [
                "network.trr.mode": mode == .strict ? 3 : 2,
                "network.trr.uri": template,
                "network.trr.custom_uri": template,
            ]
        }

        /// Chromium's browser-wide (local state) preferences.
        public var chromiumPreferences: [String: Any] {
            guard let template else { return ["dns_over_https.mode": "off", "dns_over_https.templates": ""] }
            return [
                "dns_over_https.mode": mode == .strict ? "secure" : "automatic",
                "dns_over_https.templates": template,
            ]
        }
    }

    /// An https URL with a host, optionally with an RFC 6570 `{?dns}` suffix.
    public static func isValidTemplate(_ text: String) -> Bool {
        let value = text.trimmed.replacingOccurrences(of: "{?dns}", with: "")
        guard let components = URLComponents(string: value), components.scheme == "https",
              let host = components.host, !host.isEmpty, !value.contains(" ")
        else { return false }
        return true
    }
}

private extension String {
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }
}
