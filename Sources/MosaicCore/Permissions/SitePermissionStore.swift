import Foundation

/// Remembered per-site permission decisions (camera, microphone, location…).
///
/// Owner: **engine** (it asks the engines' permission requests and records the answers);
/// the **privacy** area's settings page lists and edits them through the same API. The
/// members below are the contract; the owner implements persistence.
public final class SitePermissionStore: @unchecked Sendable {
    public enum Permission: String, CaseIterable, Codable, Sendable {
        case camera, microphone, location, notifications, popups, autoplay, clipboard
    }

    public enum Decision: String, Codable, Sendable { case allow, block }

    public struct Entry: Hashable, Codable, Sendable {
        public let site: String
        public let permission: Permission
        public let decision: Decision
    }

    public init(fileURL: URL) {}

    /// The remembered decision, or nil to ask.
    public func decision(for permission: Permission, site: String) -> Decision? { nil }

    /// Remembers (or, with nil, forgets) a decision.
    public func set(_ decision: Decision?, for permission: Permission, site: String) {}

    /// Every remembered decision, sorted by site.
    public func entries() -> [Entry] { [] }
}
