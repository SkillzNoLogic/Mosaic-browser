import Foundation

/// Mosaic's own update check, against the GitHub releases of its repository.
public enum ReleaseCheck {
    public static let repository = "SkillzNoLogic/Mosaic-browser"
    public static let latestReleaseURL =
        URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!
    public static let releasesPageURL = URL(string: "https://github.com/\(repository)/releases")!

    /// Where the latest release is looked up: GitHub, unless a test sets `MOSAIC_UPDATE_API`
    /// to another feed returning the same JSON (HTTPS, or HTTP to this Mac only).
    public static func feedURL(environment: [String: String] = ProcessInfo.processInfo.environment) -> URL {
        if let text = environment["MOSAIC_UPDATE_API"], let url = URL(string: text), isAcceptableFeedOverride(url) {
            return url
        }
        return latestReleaseURL
    }

    public struct Release: Equatable, Sendable {
        public let version: SoftwareVersion
        public let name: String
        public let pageURL: URL?
        public let publishedAt: Date?
        public let notes: String
        /// Files attached to the release (the update zip and its signature among them).
        public var assets: [Asset] = []
    }

    public enum Outcome: Equatable, Sendable {
        case upToDate(latest: SoftwareVersion)
        case available(Release)
        /// GitHub answered 404: the repository is private (or has no releases), and
        /// anonymous requests cannot see it.
        case needsAccess
        case rateLimited
        case failed(String)

        public var message: String {
            switch self {
            case .upToDate(let latest): "Mosaic is up to date (latest release: \(latest.numberString))."
            case .available(let release): "Mosaic \(release.version.numberString) is available."
            case .needsAccess:
                "Mosaic’s repository is private, so GitHub shows its releases only to people with access. Add a GitHub token below to check and update."
            case .rateLimited: "GitHub is limiting requests from your network. Try again in an hour."
            case .failed(let detail): "Could not check for updates: \(detail)"
            }
        }
    }

    /// Interprets GitHub's answer to `latestReleaseURL`.
    public static func outcome(status: Int, body: Data, current: SoftwareVersion?) -> Outcome {
        switch status {
        case 200:
            guard let release = parseRelease(body) else { return .failed("GitHub’s reply could not be read.") }
            if let current, release.version <= current { return .upToDate(latest: release.version) }
            return .available(release)
        case 404: return .needsAccess
        case 403, 429: return .rateLimited
        default: return .failed("GitHub returned HTTP \(status).")
        }
    }

    public static func parseRelease(_ data: Data) -> Release? {
        guard let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let tag = object["tag_name"] as? String, let version = SoftwareVersion(tag)
        else { return nil }
        let published = (object["published_at"] as? String).flatMap { ISO8601DateFormatter().date(from: $0) }
        return Release(
            version: version,
            name: (object["name"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? tag,
            pageURL: (object["html_url"] as? String).flatMap(URL.init(string:)),
            publishedAt: published,
            notes: object["body"] as? String ?? "",
            assets: parseAssets(data))
    }
}

/// How often Mosaic checks for updates on its own. Checks never happen in the background
/// unless the person chose `daily`.
public enum UpdateCheckFrequency: String, CaseIterable, Codable, Sendable {
    case manual, daily

    public static let interval: TimeInterval = 24 * 60 * 60

    /// Whether an automatic check is due.
    public func isDue(lastCheck: Date?, now: Date) -> Bool {
        guard self == .daily else { return false }
        guard let lastCheck else { return true }
        return now.timeIntervalSince(lastCheck) >= Self.interval || lastCheck > now
    }
}
