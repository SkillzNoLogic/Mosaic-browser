import Foundation
import Security

/// Saved passwords in the macOS Keychain (the login keychain), as internet passwords:
/// server = host, protocol = http/https, port, account = username, the password as the
/// item's secret. Nothing secret is ever written anywhere else.
///
/// Mosaic's items are told apart from every other app's by a *scope*: the security-domain
/// attribute (part of an internet password's identity, so Mosaic can never collide with or
/// overwrite another app's item for the same site and username), plus a label
/// ("example.com (Mosaic)") and a comment for people looking in Keychain Access.
///
/// An isolated test profile (`MOSAIC_DATA_DIR`) uses a scope of its own, so a test run can
/// neither see nor change the passwords of the user's real profile, and its leftovers are
/// recognisable ("… (Mosaic test profile)").
public final class KeychainPasswordStore: PasswordStore, @unchecked Sendable {
    public struct Scope: Equatable, Sendable {
        public let securityDomain: String
        public let labelSuffix: String
        public let comment: String

        public static let standard = Scope(
            securityDomain: "dev.mosaic.browser", labelSuffix: "Mosaic", comment: "Saved by Mosaic")

        /// The scope of an isolated test profile; `id` distinguishes profiles.
        public static func testProfile(_ id: String) -> Scope {
            Scope(securityDomain: "dev.mosaic.browser.test.\(id)", labelSuffix: "Mosaic test profile",
                  comment: "Saved by a Mosaic test profile; safe to delete")
        }
    }

    public let scope: Scope

    public init(scope: Scope) {
        self.scope = scope
    }

    // MARK: PasswordStore

    public func credentials() throws -> [PasswordCredential] {
        var query: [String: Any] = scopeQuery
        query[kSecMatchLimit as String] = kSecMatchLimitAll
        query[kSecReturnAttributes as String] = true
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return [] }
        try check(status)
        let items = result as? [[String: Any]] ?? []
        return items.compactMap { item in
            let server = item[kSecAttrServer as String] as? String ?? ""
            let proto = item[kSecAttrProtocol as String] as? String
            let port = (item[kSecAttrPort as String] as? NSNumber)?.intValue ?? 0
            let scheme = proto == (kSecAttrProtocolHTTP as String) ? "http" : "https"
            guard let origin = SiteOrigin(scheme: scheme, host: server, port: port == 0 ? nil : port) else { return nil }
            return PasswordCredential(
                origin: origin,
                username: item[kSecAttrAccount as String] as? String ?? "",
                created: item[kSecAttrCreationDate as String] as? Date,
                modified: item[kSecAttrModificationDate as String] as? Date)
        }
    }

    public func password(for id: CredentialID) throws -> String {
        var query = itemQuery(id)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        try check(SecItemCopyMatching(query as CFDictionary, &result))
        guard let data = result as? Data, let password = String(data: data, encoding: .utf8) else {
            throw PasswordStoreError.notFound
        }
        return password
    }

    public func add(_ id: CredentialID, password: String) throws {
        var attributes = itemQuery(id)
        attributes[kSecAttrLabel as String] = "\(id.origin.displayName) (\(scope.labelSuffix))"
        attributes[kSecAttrComment as String] = scope.comment
        attributes[kSecValueData as String] = Data(password.utf8)
        try check(SecItemAdd(attributes as CFDictionary, nil))
    }

    public func update(_ id: CredentialID, username: String?, password: String?) throws {
        var changes: [String: Any] = [:]
        if let username, username != id.username { changes[kSecAttrAccount as String] = username }
        if let password { changes[kSecValueData as String] = Data(password.utf8) }
        guard !changes.isEmpty else { return }
        try check(SecItemUpdate(itemQuery(id) as CFDictionary, changes as CFDictionary))
    }

    public func delete(_ id: CredentialID) throws {
        try check(SecItemDelete(itemQuery(id) as CFDictionary))
    }

    // MARK: Queries

    private var scopeQuery: [String: Any] {
        [
            kSecClass as String: kSecClassInternetPassword,
            kSecAttrSecurityDomain as String: scope.securityDomain,
        ]
    }

    private func itemQuery(_ id: CredentialID) -> [String: Any] {
        var query = scopeQuery
        query[kSecAttrServer as String] = id.origin.host
        query[kSecAttrProtocol as String] = id.origin.scheme == "http" ? kSecAttrProtocolHTTP : kSecAttrProtocolHTTPS
        query[kSecAttrPort as String] = id.origin.port ?? 0
        query[kSecAttrAccount as String] = id.username
        return query
    }

    private func check(_ status: OSStatus) throws {
        switch status {
        case errSecSuccess: return
        case errSecDuplicateItem: throw PasswordStoreError.duplicate
        case errSecItemNotFound: throw PasswordStoreError.notFound
        case errSecUserCanceled, errSecAuthFailed, errSecInteractionNotAllowed: throw PasswordStoreError.cancelled
        default:
            let message = SecCopyErrorMessageString(status, nil) as String? ?? "unknown error"
            throw PasswordStoreError.keychain(status, message)
        }
    }
}
