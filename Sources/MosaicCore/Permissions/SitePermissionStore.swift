import Foundation

/// Remembered per-site permission decisions (camera, microphone, location…).
///
/// Owner: **engine** (it asks the engines' permission requests and records the answers);
/// the **privacy** area's settings page lists and edits them through the same API.
///
/// Decisions are kept per *site*, which is a web origin: `https://example.com`,
/// `http://localhost:8000` (scheme, host and a non-default port, lower-case). Both engines
/// scope permissions to origins, so one decision applies to Chromium and Gecko tabs alike.
/// The store persists to a JSON file in the profile and is safe to use from any thread.
public final class SitePermissionStore: @unchecked Sendable {
    public enum Permission: String, CaseIterable, Codable, Sendable {
        case camera, microphone, location, notifications, popups, autoplay, clipboard
    }

    public enum Decision: String, Codable, Sendable { case allow, block }

    public struct Entry: Hashable, Codable, Sendable {
        public let site: String
        public let permission: Permission
        public let decision: Decision

        public init(site: String, permission: Permission, decision: Decision) {
            self.site = site
            self.permission = permission
            self.decision = decision
        }
    }

    /// Posted (on the thread that made the change) whenever a decision is added, changed or
    /// removed. `object` is the store; `userInfo["entry"]` is the changed `Entry` when one was
    /// set, absent when one was forgotten; `userInfo["site"]`/`["permission"]` always name it.
    public static let didChangeNotification = Notification.Name("dev.mosaic.SitePermissionStore.didChange")

    public let fileURL: URL
    private let lock = NSLock()
    /// site → permission → decision.
    private var decisions: [String: [Permission: Decision]] = [:]

    public init(fileURL: URL) {
        self.fileURL = fileURL
        load()
    }

    /// The remembered decision, or nil to ask. `site` may be an origin or any URL on it.
    public func decision(for permission: Permission, site: String) -> Decision? {
        guard let key = Self.site(for: site) else { return nil }
        lock.lock()
        defer { lock.unlock() }
        return decisions[key]?[permission]
    }

    /// Remembers (or, with nil, forgets) a decision. `site` may be an origin or any URL on it;
    /// values that are not web sites are ignored.
    public func set(_ decision: Decision?, for permission: Permission, site: String) {
        guard let key = Self.site(for: site) else { return }
        lock.lock()
        let previous = decisions[key]?[permission]
        guard previous != decision else {
            lock.unlock()
            return
        }
        if let decision {
            decisions[key, default: [:]][permission] = decision
        } else {
            decisions[key]?[permission] = nil
            if decisions[key]?.isEmpty == true { decisions[key] = nil }
        }
        // Written under the lock so concurrent changes reach the file in order.
        save(decisions)
        lock.unlock()

        var info: [String: Any] = ["site": key, "permission": permission]
        if let decision { info["entry"] = Entry(site: key, permission: permission, decision: decision) }
        NotificationCenter.default.post(name: Self.didChangeNotification, object: self, userInfo: info)
    }

    /// Every remembered decision, sorted by site (then by permission).
    public func entries() -> [Entry] {
        lock.lock()
        let snapshot = decisions
        lock.unlock()
        return Self.entries(from: snapshot)
    }

    /// Forgets every decision for `site` (an origin or any URL on it).
    public func removeAll(forSite site: String) {
        guard let key = Self.site(for: site) else { return }
        lock.lock()
        let permissions = decisions[key].map { Array($0.keys) } ?? []
        lock.unlock()
        for permission in permissions { set(nil, for: permission, site: key) }
    }

    // MARK: - Sites

    /// The site (web origin) a URL belongs to, or nil if it is not on the web. Accepts a full
    /// URL, an origin, or a bare host name (taken as `https`). `file:` URLs share the single
    /// site `file://`, as they do in both engines.
    public static func site(for url: String) -> String? {
        let trimmed = url.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let text = trimmed.contains("://") ? trimmed : "https://" + trimmed
        guard let components = URLComponents(string: text), let scheme = components.scheme?.lowercased()
        else { return nil }
        if scheme == "file" { return "file://" }
        guard scheme == "http" || scheme == "https",
              let host = components.host?.lowercased(), !host.isEmpty, !host.contains(" ")
        else { return nil }
        let defaultPort = scheme == "https" ? 443 : 80
        let hostPart = host.contains(":") && !host.hasPrefix("[") ? "[\(host)]" : host
        if let port = components.port, port != defaultPort {
            return "\(scheme)://\(hostPart):\(port)"
        }
        return "\(scheme)://\(hostPart)"
    }

    /// The site shown to people: the host without `https://` (other schemes are kept, so
    /// an insecure `http://` site stays recognisable).
    public static func displayName(forSite site: String) -> String {
        if site == "file://" { return "Local files" }
        if site.hasPrefix("https://") { return String(site.dropFirst("https://".count)) }
        return site
    }

    // MARK: - Persistence

    private struct FileFormat: Codable {
        var version = 1
        var entries: [Entry]
    }

    private static func entries(from decisions: [String: [Permission: Decision]]) -> [Entry] {
        let order = Dictionary(uniqueKeysWithValues: Permission.allCases.enumerated().map { ($1, $0) })
        return decisions.flatMap { site, permissions in
            permissions.map { Entry(site: site, permission: $0.key, decision: $0.value) }
        }
        .sorted { ($0.site, order[$0.permission] ?? 0) < ($1.site, order[$1.permission] ?? 0) }
    }

    private func load() {
        guard let data = try? Data(contentsOf: fileURL),
              let file = try? JSONDecoder().decode(FileFormat.self, from: data)
        else { return }
        var loaded: [String: [Permission: Decision]] = [:]
        for entry in file.entries {
            guard let key = Self.site(for: entry.site) else { continue }
            loaded[key, default: [:]][entry.permission] = entry.decision
        }
        lock.lock()
        decisions = loaded
        lock.unlock()
    }

    private func save(_ snapshot: [String: [Permission: Decision]]) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(FileFormat(entries: Self.entries(from: snapshot))) else { return }
        try? FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}
