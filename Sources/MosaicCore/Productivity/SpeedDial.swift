import Foundation

/// One tile on the new-tab page.
public struct SpeedDialSite: Identifiable, Hashable, Codable, Sendable {
    public var id: UUID
    public var title: String
    public var url: String

    public init(id: UUID = UUID(), title: String, url: String) {
        self.id = id
        self.title = title
        self.url = url
    }

    /// The host without "www.", used for de-duplication, letter tiles and icons.
    public var host: String { SpeedDial.host(of: url) ?? url }

    /// Title, or the host when the user left the title empty.
    public var displayTitle: String {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? host : trimmed
    }
}

/// What the new-tab grid shows: the user's pinned sites, then the most visited sites the
/// user has not pinned or removed.
public struct SpeedDial: Equatable, Codable, Sendable {
    public private(set) var pinned: [SpeedDialSite]
    /// Normalised addresses of top sites the user removed from the grid.
    public private(set) var hiddenTopSites: Set<String>
    public var showsTopSites: Bool
    /// Fetch site icons (directly from each site, no third party); off shows letter tiles.
    public var showsIcons: Bool

    public init(pinned: [SpeedDialSite] = [], hiddenTopSites: Set<String> = [],
                showsTopSites: Bool = true, showsIcons: Bool = true) {
        self.pinned = pinned
        self.hiddenTopSites = hiddenTopSites
        self.showsTopSites = showsTopSites
        self.showsIcons = showsIcons
    }

    private enum CodingKeys: String, CodingKey { case pinned, hiddenTopSites, showsTopSites, showsIcons }

    /// Tolerates files written by older versions (missing keys take their defaults).
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        pinned = try container.decodeIfPresent([SpeedDialSite].self, forKey: .pinned) ?? []
        hiddenTopSites = try container.decodeIfPresent(Set<String>.self, forKey: .hiddenTopSites) ?? []
        showsTopSites = try container.decodeIfPresent(Bool.self, forKey: .showsTopSites) ?? true
        showsIcons = try container.decodeIfPresent(Bool.self, forKey: .showsIcons) ?? true
    }

    public enum EditError: Error, Equatable {
        case invalidAddress
        case duplicate
    }

    // MARK: Addresses

    /// Turns what the user typed into an address: adds https:// to bare hosts, refuses
    /// anything that is not http(s) (no javascript:, file:, data:).
    public static func normalizedURL(_ input: String) -> String? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !text.contains(where: \.isWhitespace) else { return nil }
        if !text.contains("://") { text = "https://" + text }
        guard var components = URLComponents(string: text),
              let scheme = components.scheme?.lowercased(), scheme == "https" || scheme == "http",
              let host = components.host, !host.isEmpty,
              host.contains(".") || host == "localhost" || components.port != nil
        else { return nil }
        components.scheme = scheme
        components.host = host.lowercased()
        if components.path.isEmpty { components.path = "/" }
        return components.string
    }

    /// The host of `url` without "www.".
    public static func host(of url: String) -> String? {
        guard let host = URLComponents(string: url)?.host?.lowercased(), !host.isEmpty else { return nil }
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }

    /// Key for "same site" comparisons: host without www, path without trailing slash,
    /// query kept, scheme and fragment ignored.
    public static func identity(of url: String) -> String {
        guard let components = URLComponents(string: url), let host = host(of: url) else { return url.lowercased() }
        var path = components.path
        while path.hasSuffix("/") { path.removeLast() }
        let query = components.percentEncodedQuery.map { "?" + $0 } ?? ""
        return host + path + query
    }

    // MARK: Editing pinned sites

    public func contains(url: String) -> Bool {
        let key = Self.identity(of: url)
        return pinned.contains { Self.identity(of: $0.url) == key }
    }

    /// Pins a site at the end (or at `index`). Returns the stored site.
    @discardableResult
    public mutating func add(title: String, url: String, at index: Int? = nil) throws -> SpeedDialSite {
        guard let normalized = Self.normalizedURL(url) else { throw EditError.invalidAddress }
        guard !contains(url: normalized) else { throw EditError.duplicate }
        let site = SpeedDialSite(title: title.trimmingCharacters(in: .whitespacesAndNewlines), url: normalized)
        let position = min(max(index ?? pinned.count, 0), pinned.count)
        pinned.insert(site, at: position)
        hiddenTopSites.remove(Self.identity(of: normalized))
        return site
    }

    public mutating func update(_ id: UUID, title: String, url: String) throws {
        guard let index = pinned.firstIndex(where: { $0.id == id }) else { return }
        guard let normalized = Self.normalizedURL(url) else { throw EditError.invalidAddress }
        let key = Self.identity(of: normalized)
        if pinned.contains(where: { $0.id != id && Self.identity(of: $0.url) == key }) { throw EditError.duplicate }
        pinned[index].title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        pinned[index].url = normalized
    }

    public mutating func remove(_ id: UUID) {
        pinned.removeAll { $0.id == id }
    }

    /// Moves the site with `id` to `destination` (an index in the list after removal).
    public mutating func move(_ id: UUID, to destination: Int) {
        guard let index = pinned.firstIndex(where: { $0.id == id }) else { return }
        let site = pinned.remove(at: index)
        pinned.insert(site, at: min(max(destination, 0), pinned.count))
    }

    /// Moves the site one place towards the start (-1) or the end (+1).
    public mutating func shift(_ id: UUID, by offset: Int) {
        guard let index = pinned.firstIndex(where: { $0.id == id }) else { return }
        move(id, to: index + offset)
    }

    // MARK: Top sites

    public mutating func hideTopSite(url: String) {
        hiddenTopSites.insert(Self.identity(of: url))
    }

    public mutating func restoreHiddenTopSites() {
        hiddenTopSites.removeAll()
    }

    /// The top sites to show after the pinned ones: not pinned, not hidden, not duplicated,
    /// http(s) only, at most `limit`.
    public func visibleTopSites(_ candidates: [(title: String, url: String)], limit: Int) -> [SpeedDialSite] {
        guard showsTopSites, limit > 0 else { return [] }
        var seen = Set(pinned.map { Self.identity(of: $0.url) })
        var result: [SpeedDialSite] = []
        for candidate in candidates {
            guard let scheme = URLComponents(string: candidate.url)?.scheme?.lowercased(),
                  scheme == "http" || scheme == "https" else { continue }
            let key = Self.identity(of: candidate.url)
            guard !hiddenTopSites.contains(key), seen.insert(key).inserted else { continue }
            // A stable id per address so SwiftUI keeps tiles in place between refreshes.
            result.append(SpeedDialSite(id: Self.stableID(for: key), title: candidate.title, url: candidate.url))
            if result.count == limit { break }
        }
        return result
    }

    /// A UUID derived from a string (FNV-1a over two seeds), stable across launches.
    static func stableID(for key: String) -> UUID {
        func fnv(_ seed: UInt64) -> UInt64 {
            var hash = seed
            for byte in key.utf8 {
                hash ^= UInt64(byte)
                hash = hash &* 0x100000001b3
            }
            return hash
        }
        let a = fnv(0xcbf29ce484222325), b = fnv(0x84222325cbf29ce4)
        var bytes = [UInt8](repeating: 0, count: 16)
        for i in 0..<8 {
            bytes[i] = UInt8(truncatingIfNeeded: a >> (UInt64(i) * 8))
            bytes[8 + i] = UInt8(truncatingIfNeeded: b >> (UInt64(i) * 8))
        }
        return UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
                           bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]))
    }
}

/// Keeps `SpeedDial` in a JSON file in the profile.
public struct SpeedDialStore {
    public let fileURL: URL

    public init(fileURL: URL) {
        self.fileURL = fileURL
    }

    public func load() -> SpeedDial {
        guard let data = try? Data(contentsOf: fileURL),
              let dial = try? JSONDecoder().decode(SpeedDial.self, from: data)
        else { return SpeedDial() }
        return dial
    }

    public func save(_ dial: SpeedDial) throws {
        try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(dial).write(to: fileURL, options: .atomic)
    }
}
