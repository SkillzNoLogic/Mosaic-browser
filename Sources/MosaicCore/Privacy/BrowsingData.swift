import Foundation

// MARK: - Clearing browsing data
//
// The Clear Browsing Data sheet (Settings › Privacy & Security) clears what both
// engines hold, and also Mosaic's own records: the history list (library area),
// the downloads list (downloads area) and anything else that keeps a record of
// browsing. Those services register here; the privacy area never calls into
// them directly.
//
// Adopting it, e.g. in the library:
//
//     extension Library: BrowsingDataClearing {
//         var browsingDataName: String { "Mosaic history" }
//         var clearableBrowsingData: Set<BrowsingDataKind> { [.history] }
//         func clearBrowsingData(_ kinds: Set<BrowsingDataKind>, since: Date?) async {
//             deleteHistory(since: since)        // nil = everything
//         }
//     }
//     // once, when the service starts:
//     BrowsingDataRegistry.shared.register(library)
//
// The registry holds services weakly, so registering never extends a lifetime.

/// The kinds of data the sheet offers.
public enum BrowsingDataKind: String, CaseIterable, Codable, Sendable, Identifiable {
    /// Pages visited (Mosaic's history, Gecko's history, back/forward lists).
    case history
    /// Cookies, local storage, IndexedDB, service workers, cache storage.
    case cookiesAndSiteData
    /// The HTTP cache.
    case cache
    /// The list of past downloads (never the downloaded files themselves).
    case downloads
    /// Saved form entries (not passwords).
    case formData

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .history: "Browsing history"
        case .cookiesAndSiteData: "Cookies and site data"
        case .cache: "Cached images and files"
        case .downloads: "Download history"
        case .formData: "Saved form entries"
        }
    }
}

/// How far back to clear.
public enum BrowsingDataTimeRange: String, CaseIterable, Codable, Sendable, Identifiable {
    case lastHour, last24Hours, last7Days, last4Weeks, allTime

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .lastHour: "Last hour"
        case .last24Hours: "Last 24 hours"
        case .last7Days: "Last 7 days"
        case .last4Weeks: "Last 4 weeks"
        case .allTime: "All time"
        }
    }

    /// The earliest moment to clear from; nil means everything.
    public func startDate(now: Date = Date()) -> Date? {
        switch self {
        case .lastHour: now.addingTimeInterval(-3600)
        case .last24Hours: now.addingTimeInterval(-86_400)
        case .last7Days: now.addingTimeInterval(-7 * 86_400)
        case .last4Weeks: now.addingTimeInterval(-28 * 86_400)
        case .allTime: nil
        }
    }
}

/// A service that keeps browsing data and can delete it on request.
@MainActor
public protocol BrowsingDataClearing: AnyObject {
    /// What the sheet calls this store, e.g. "Mosaic history".
    var browsingDataName: String { get }
    /// The kinds this service holds; it is only asked about these.
    var clearableBrowsingData: Set<BrowsingDataKind> { get }
    /// Deletes data of `kinds` (a subset of `clearableBrowsingData`) recorded at
    /// or after `since`; everything when `since` is nil. Return when done.
    func clearBrowsingData(_ kinds: Set<BrowsingDataKind>, since: Date?) async
}

/// Where services that keep browsing data register (see `BrowsingDataClearing`).
@MainActor
public final class BrowsingDataRegistry {
    public static let shared = BrowsingDataRegistry()

    private struct Entry {
        weak var clearer: (any BrowsingDataClearing)?
    }

    private var entries: [Entry] = []

    public init() {}

    /// Adds a service. Registering the same object twice has no effect.
    public func register(_ clearer: any BrowsingDataClearing) {
        entries.removeAll { $0.clearer == nil }
        guard !entries.contains(where: { $0.clearer === clearer }) else { return }
        entries.append(Entry(clearer: clearer))
    }

    public func unregister(_ clearer: any BrowsingDataClearing) {
        entries.removeAll { $0.clearer == nil || $0.clearer === clearer }
    }

    /// Services still alive, in registration order.
    public var clearers: [any BrowsingDataClearing] { entries.compactMap(\.clearer) }

    /// Kinds at least one registered service holds.
    public var coveredKinds: Set<BrowsingDataKind> {
        clearers.reduce(into: Set()) { $0.formUnion($1.clearableBrowsingData) }
    }

    /// Asks every service holding any of `kinds` to clear them, one after the
    /// other. Returns the names of the services asked.
    @discardableResult
    public func clear(_ kinds: Set<BrowsingDataKind>, since: Date?) async -> [String] {
        var asked: [String] = []
        for clearer in clearers {
            let relevant = kinds.intersection(clearer.clearableBrowsingData)
            guard !relevant.isEmpty else { continue }
            await clearer.clearBrowsingData(relevant, since: since)
            asked.append(clearer.browsingDataName)
        }
        return asked
    }
}
