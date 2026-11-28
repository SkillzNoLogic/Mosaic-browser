import Foundation

/// Page zoom shared by both engines: the steps ⌘+ / ⌘- move through, conversion to
/// Chromium's zoom levels, and the site a zoom choice is remembered for.
public enum PageZoom {
    /// Zoom factors ⌘+ and ⌘- step through: Chrome's familiar steps, limited to the range
    /// both engines support (Gecko's minimum is 30 %).
    public static let steps: [Double] = [
        0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0,
    ]
    public static let minimum = 0.33
    public static let maximum = 5.0

    /// The next step above `factor` (or the largest step).
    public static func zoomedIn(from factor: Double) -> Double {
        steps.first { $0 > factor + 0.001 } ?? maximum
    }

    /// The next step below `factor` (or the smallest step).
    public static func zoomedOut(from factor: Double) -> Double {
        steps.last { $0 < factor - 0.001 } ?? minimum
    }

    public static func clamped(_ factor: Double) -> Double {
        guard factor.isFinite, factor > 0 else { return 1 }
        return min(max(factor, minimum), maximum)
    }

    /// True when `factor` is the default size, within rounding.
    public static func isDefault(_ factor: Double) -> Bool { abs(factor - 1) < 0.005 }

    /// Chromium expresses zoom as a level: each level is a factor of 1.2, 0 is 100 %.
    public static func chromiumLevel(forFactor factor: Double) -> Double {
        log(clamped(factor)) / log(1.2)
    }

    public static func factor(forChromiumLevel level: Double) -> Double {
        pow(1.2, level)
    }

    /// "110 %" style label.
    public static func percentLabel(_ factor: Double) -> String {
        "\(Int((factor * 100).rounded())) %"
    }

    /// The key a zoom choice is remembered under: the page's host without `www.`, so a
    /// site keeps its zoom across its pages, schemes and both engines. Nil for pages that
    /// are not on the web.
    public static func site(for url: String) -> String? {
        guard let components = URLComponents(string: url),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              var host = components.host?.lowercased(), !host.isEmpty
        else { return nil }
        if host.hasPrefix("www.") { host.removeFirst(4) }
        return host
    }
}

/// Zoom factors remembered per site (see `PageZoom.site(for:)`), persisted as JSON.
/// Sites at 100 % are not stored. Main-thread use.
public final class SiteZoomStore {
    public let fileURL: URL
    private var factors: [String: Double] = [:]

    public init(fileURL: URL) {
        self.fileURL = fileURL
        if let data = try? Data(contentsOf: fileURL),
           let stored = try? JSONDecoder().decode([String: Double].self, from: data)
        {
            factors = stored.filter { $0.value.isFinite && $0.value > 0 }
        }
    }

    /// The remembered factor for the site of `url`; 1 when none is remembered.
    public func factor(for url: String) -> Double {
        guard let site = PageZoom.site(for: url) else { return 1 }
        return factors[site] ?? 1
    }

    /// Remembers `factor` for the site of `url` (100 % forgets it). Returns false for pages
    /// that are not on the web, whose zoom is not remembered.
    @discardableResult
    public func setFactor(_ factor: Double, for url: String) -> Bool {
        guard let site = PageZoom.site(for: url) else { return false }
        let value = PageZoom.clamped(factor)
        let newValue: Double? = PageZoom.isDefault(value) ? nil : value
        guard factors[site] != newValue else { return true }
        factors[site] = newValue
        save()
        return true
    }

    /// Every site with a remembered zoom, sorted.
    public var sites: [(site: String, factor: Double)] {
        factors.sorted { $0.key < $1.key }.map { ($0.key, $0.value) }
    }

    private func save() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(factors) else { return }
        try? FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}

/// What the find bar says about the matches of the current search.
public struct FindStatus: Equatable, Sendable {
    /// 1-based position of the highlighted match; 0 when unknown or none.
    public var current: Int
    /// Number of matches; `limit` > 0 means the engine stopped counting there.
    public var total: Int
    public var limit: Int

    public init(current: Int, total: Int, limit: Int = 0) {
        self.current = current
        self.total = total
        self.limit = limit
    }

    public var hasMatches: Bool { total > 0 }

    /// "3 of 12", "1 of 1000+", or "No matches".
    public var label: String {
        guard total > 0 else { return "No matches" }
        let capped = limit > 0 && total >= limit
        let count = capped ? "\(limit)+" : "\(total)"
        return current > 0 ? "\(current) of \(count)" : (capped ? "More than \(limit) matches" : "\(count) matches")
    }

    /// The same, spelled out for VoiceOver.
    public var accessibilityLabel: String {
        guard total > 0 else { return "No matches" }
        if current > 0 { return "Match \(current) of \(limit > 0 && total >= limit ? "more than \(limit)" : "\(total)")" }
        return label
    }
}
