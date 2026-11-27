import Foundation

/// A dotted version number as Mozilla and GitHub releases write them: "153.8.0esr",
/// "153.8.0", "140.0b3", "v0.2.0". Comparison is numeric, component by component;
/// missing components count as zero ("153.8" == "153.8.0").
///
/// A pre-release marker ("b3", "a1", "rc2", "-beta.1") sorts before the plain release
/// of the same number. The "esr" suffix is not a pre-release: "153.8.0esr" is the same
/// release as "153.8.0" (Mozilla's product details carry the suffix, the app's
/// Info.plist does not).
public struct SoftwareVersion: Comparable, Hashable, Sendable, CustomStringConvertible {
    public let components: [Int]
    /// Pre-release label, lowercased ("b3", "beta.1"), or nil for a release.
    public let preRelease: String?
    public let original: String

    public init?(_ text: String) {
        var rest = text.trimmingCharacters(in: .whitespacesAndNewlines)
        original = rest
        if rest.first == "v" || rest.first == "V" { rest.removeFirst() }
        let lowered = rest.lowercased()
        if lowered.hasSuffix("esr") { rest = String(rest.dropLast(3)) }

        // Leading run of digits and dots is the number; anything after is a label.
        let numberPart = rest.prefix { $0.isNumber || $0 == "." }
        var label = String(rest.dropFirst(numberPart.count)).lowercased()
        while label.first == "-" || label.first == "+" || label.first == "." { label.removeFirst() }

        let parts = numberPart.split(separator: ".", omittingEmptySubsequences: false)
        guard !parts.isEmpty, !parts.contains(where: \.isEmpty) else { return nil }
        let numbers = parts.compactMap { Int($0) }
        guard numbers.count == parts.count else { return nil }
        components = numbers
        preRelease = label.isEmpty ? nil : label
    }

    public var description: String { original }

    /// The number alone, e.g. "153.8.0".
    public var numberString: String { components.map(String.init).joined(separator: ".") }

    public static func == (lhs: SoftwareVersion, rhs: SoftwareVersion) -> Bool {
        compare(lhs, rhs) == .orderedSame
    }

    public func hash(into hasher: inout Hasher) {
        var trimmed = components
        while trimmed.last == 0 { trimmed.removeLast() }
        hasher.combine(trimmed)
        hasher.combine(preRelease)
    }

    public static func < (lhs: SoftwareVersion, rhs: SoftwareVersion) -> Bool {
        compare(lhs, rhs) == .orderedAscending
    }

    private static func compare(_ lhs: SoftwareVersion, _ rhs: SoftwareVersion) -> ComparisonResult {
        let count = max(lhs.components.count, rhs.components.count)
        for index in 0..<count {
            let left = index < lhs.components.count ? lhs.components[index] : 0
            let right = index < rhs.components.count ? rhs.components[index] : 0
            if left != right { return left < right ? .orderedAscending : .orderedDescending }
        }
        switch (lhs.preRelease, rhs.preRelease) {
        case (nil, nil): return .orderedSame
        case (nil, _): return .orderedDescending
        case (_, nil): return .orderedAscending
        case let (left?, right?):
            if left == right { return .orderedSame }
            return left.compare(right, options: .numeric) == .orderedAscending ? .orderedAscending : .orderedDescending
        }
    }
}

/// Mozilla's public release metadata (`product-details.mozilla.org`).
public enum FirefoxProductDetails {
    /// Current Firefox versions, one JSON object of channel → version.
    public static let versionsURL = URL(string: "https://product-details.mozilla.org/1.0/firefox_versions.json")!

    /// Latest Firefox ESR for macOS (a universal build, so it runs natively on Apple
    /// Silicon), English (US). Mozilla redirects this to its CDN, over HTTPS.
    public static let esrDownloadURL =
        URL(string: "https://download.mozilla.org/?product=firefox-esr-latest-ssl&os=osx&lang=en-US")!

    /// The current ESR version from `firefox_versions.json`, e.g. "153.8.0esr".
    public static func esrVersion(from data: Data) -> SoftwareVersion? {
        guard let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let text = object["FIREFOX_ESR"] as? String, !text.isEmpty
        else { return nil }
        return SoftwareVersion(text)
    }

    /// Hosts a runtime download may come from (the redirector and its CDN). A redirect
    /// anywhere else, or to plain HTTP, is refused.
    public static func isTrustedDownloadURL(_ url: URL) -> Bool {
        guard url.scheme?.lowercased() == "https", let host = url.host?.lowercased() else { return false }
        return ["mozilla.org", "mozilla.net"].contains { host == $0 || host.hasSuffix("." + $0) }
    }
}
