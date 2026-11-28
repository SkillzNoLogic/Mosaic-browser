import Foundation

/// Identifies a saved password: one per origin and username.
public struct CredentialID: Hashable, Codable, Sendable, Comparable, CustomStringConvertible {
    public let origin: SiteOrigin
    public let username: String

    public init(origin: SiteOrigin, username: String) {
        self.origin = origin
        self.username = username
    }

    /// Stable text form, used as a key for non-secret metadata (last used).
    public var key: String { "\(origin.string) \(username)" }
    public var description: String { key }

    public static func < (lhs: CredentialID, rhs: CredentialID) -> Bool {
        (lhs.origin, lhs.username.lowercased()) < (rhs.origin, rhs.username.lowercased())
    }
}

/// A saved password's non-secret attributes. The password itself is only read on demand.
public struct PasswordCredential: Identifiable, Hashable, Sendable {
    public let origin: SiteOrigin
    public let username: String
    public var created: Date?
    public var modified: Date?

    public init(origin: SiteOrigin, username: String, created: Date? = nil, modified: Date? = nil) {
        self.origin = origin
        self.username = username
        self.created = created
        self.modified = modified
    }

    public var id: CredentialID { CredentialID(origin: origin, username: username) }
}

public enum PasswordStoreError: LocalizedError, Equatable {
    case duplicate
    case notFound
    case cancelled
    case keychain(OSStatus, String)

    public var errorDescription: String? {
        switch self {
        case .duplicate: "A password for this username is already saved for this site."
        case .notFound: "The password is no longer saved."
        case .cancelled: "Access to the password was not allowed."
        case .keychain(let status, let message): "Keychain error \(status): \(message)"
        }
    }
}

/// Where passwords live. The app uses `KeychainPasswordStore`; tests use
/// `InMemoryPasswordStore` so they never touch the user's Keychain.
public protocol PasswordStore: AnyObject {
    /// Every saved credential (no secrets), in no particular order.
    func credentials() throws -> [PasswordCredential]
    func password(for id: CredentialID) throws -> String
    /// Throws `.duplicate` if the origin and username are already saved.
    func add(_ id: CredentialID, password: String) throws
    /// Changes the username and/or the password of a saved credential.
    func update(_ id: CredentialID, username: String?, password: String?) throws
    func delete(_ id: CredentialID) throws
}

/// A store that keeps everything in memory. For tests and previews.
public final class InMemoryPasswordStore: PasswordStore, @unchecked Sendable {
    private struct Item {
        var password: String
        var created: Date
        var modified: Date
    }

    private var items: [CredentialID: Item] = [:]
    private let lock = NSLock()
    private let now: () -> Date

    public init(now: @escaping () -> Date = Date.init) {
        self.now = now
    }

    public func credentials() throws -> [PasswordCredential] {
        lock.withLock {
            items.map { PasswordCredential(origin: $0.key.origin, username: $0.key.username,
                                           created: $0.value.created, modified: $0.value.modified) }
        }
    }

    public func password(for id: CredentialID) throws -> String {
        try lock.withLock {
            guard let item = items[id] else { throw PasswordStoreError.notFound }
            return item.password
        }
    }

    public func add(_ id: CredentialID, password: String) throws {
        try lock.withLock {
            guard items[id] == nil else { throw PasswordStoreError.duplicate }
            let date = now()
            items[id] = Item(password: password, created: date, modified: date)
        }
    }

    public func update(_ id: CredentialID, username: String?, password: String?) throws {
        try lock.withLock {
            guard var item = items[id] else { throw PasswordStoreError.notFound }
            let newID = CredentialID(origin: id.origin, username: username ?? id.username)
            if newID != id, items[newID] != nil { throw PasswordStoreError.duplicate }
            if let password { item.password = password }
            item.modified = now()
            items[id] = nil
            items[newID] = item
        }
    }

    public func delete(_ id: CredentialID) throws {
        try lock.withLock {
            guard items.removeValue(forKey: id) != nil else { throw PasswordStoreError.notFound }
        }
    }
}
