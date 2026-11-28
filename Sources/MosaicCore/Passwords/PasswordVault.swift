import Foundation

/// Non-secret password settings and metadata, kept in a JSON file in Mosaic's data folder.
/// Never holds a password.
public struct PasswordPreferences: Codable, Equatable, Sendable {
    /// Offer to save passwords when a sign-in or sign-up form is submitted.
    public var offerToSave = true
    /// Suggest a generated password when a new-password field is focused.
    public var suggestStrongPasswords = true
    /// Sites the user chose "Never for This Site" on.
    public var neverSave: [SiteOrigin] = []
    /// When each credential was last filled or confirmed by a sign-in, by `CredentialID.key`.
    public var lastUsed: [String: Date] = [:]
    public var generator = PasswordGeneratorOptions.default

    public init() {}

    public init(from decoder: Decoder) throws {
        // Tolerant: a missing or unreadable key keeps its default instead of losing the file.
        let container = try decoder.container(keyedBy: CodingKeys.self)
        offerToSave = (try? container.decode(Bool.self, forKey: .offerToSave)) ?? true
        suggestStrongPasswords = (try? container.decode(Bool.self, forKey: .suggestStrongPasswords)) ?? true
        neverSave = (try? container.decode([SiteOrigin].self, forKey: .neverSave)) ?? []
        lastUsed = (try? container.decode([String: Date].self, forKey: .lastUsed)) ?? [:]
        generator = (try? container.decode(PasswordGeneratorOptions.self, forKey: .generator)) ?? .default
    }
}

/// Loads and saves `PasswordPreferences`.
public final class PasswordPreferencesStore: @unchecked Sendable {
    public let fileURL: URL

    public init(fileURL: URL) {
        self.fileURL = fileURL
    }

    public func load() -> PasswordPreferences {
        guard let data = try? Data(contentsOf: fileURL) else { return PasswordPreferences() }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode(PasswordPreferences.self, from: data)) ?? PasswordPreferences()
    }

    public func save(_ preferences: PasswordPreferences) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try encoder.encode(preferences).write(to: fileURL, options: .atomic)
    }
}

/// A sign-in or sign-up the page reported, with the origin established by the engine
/// (never by the page).
public struct PasswordSubmission: Equatable, Sendable {
    public enum Mode: String, Sendable {
        /// Signing in: username and current password.
        case login
        /// Creating an account: the new password (typed twice, or marked `new-password`).
        case signup
        /// Changing a password: the new one replaces the saved one.
        case change
    }

    public let origin: SiteOrigin
    public let username: String
    public let password: String
    public let mode: Mode

    public init(origin: SiteOrigin, username: String, password: String, mode: Mode = .login) {
        self.origin = origin
        self.username = username
        self.password = password
        self.mode = mode
    }
}

/// What to ask after a submission.
public enum SaveOffer: Equatable, Sendable {
    /// Nothing saved for this username yet.
    case save
    /// A saved password for this site and username differs from the one just used.
    case update(CredentialID)
}

/// The password manager's rules, over a `PasswordStore` and the preferences. Engine- and
/// UI-independent; the app drives it from the main thread.
public final class PasswordVault {
    public let store: PasswordStore
    private let preferencesStore: PasswordPreferencesStore?
    public private(set) var preferences: PasswordPreferences
    /// Cached list of saved credentials (metadata only), refreshed after every change.
    public private(set) var credentials: [PasswordCredential] = []
    public private(set) var lastError: String?

    public init(store: PasswordStore, preferencesStore: PasswordPreferencesStore?) {
        self.store = store
        self.preferencesStore = preferencesStore
        preferences = preferencesStore?.load() ?? PasswordPreferences()
        reload()
    }

    // MARK: Reading

    public func reload() {
        do {
            credentials = try store.credentials().sorted { $0.id < $1.id }
            lastError = nil
        } catch {
            credentials = []
            lastError = error.localizedDescription
        }
    }

    /// Credentials saved for exactly `origin`, most recently used first.
    public func matches(for origin: SiteOrigin) -> [PasswordCredential] {
        credentials.filter { $0.origin == origin }.sorted { lhs, rhs in
            let l = lastUsed(lhs.id) ?? .distantPast, r = lastUsed(rhs.id) ?? .distantPast
            return l != r ? l > r : lhs.id < rhs.id
        }
    }

    /// Case-insensitive search over site and username.
    public func search(_ text: String) -> [PasswordCredential] {
        let query = text.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return credentials }
        return credentials.filter {
            $0.origin.string.localizedCaseInsensitiveContains(query) || $0.username.localizedCaseInsensitiveContains(query)
        }
    }

    public func password(for id: CredentialID) throws -> String {
        try store.password(for: id)
    }

    public func lastUsed(_ id: CredentialID) -> Date? { preferences.lastUsed[id.key] }

    // MARK: Changing

    public enum SaveOutcome: Equatable, Sendable { case added, updated, unchanged }

    /// Adds a credential, or replaces the password of the one with the same origin and username.
    /// `markUsed` records it as just used (a sign-in); imports pass false.
    @discardableResult
    public func save(_ id: CredentialID, password: String, markUsed used: Bool = true) throws -> SaveOutcome {
        let outcome: SaveOutcome
        if credentials.contains(where: { $0.id == id }) {
            if (try? store.password(for: id)) == password {
                outcome = .unchanged
            } else {
                try store.update(id, username: nil, password: password)
                outcome = .updated
            }
        } else {
            try store.add(id, password: password)
            outcome = .added
        }
        if used { markUsed(id) }
        reload()
        return outcome
    }

    /// Edits a credential. The origin cannot change (that would be a different credential).
    public func edit(_ id: CredentialID, username: String, password: String?) throws {
        try store.update(id, username: username, password: password)
        let newID = CredentialID(origin: id.origin, username: username)
        if newID != id, let used = preferences.lastUsed.removeValue(forKey: id.key) {
            preferences.lastUsed[newID.key] = used
            persist()
        }
        reload()
    }

    public func delete(_ id: CredentialID) throws {
        try store.delete(id)
        if preferences.lastUsed.removeValue(forKey: id.key) != nil { persist() }
        reload()
    }

    public func markUsed(_ id: CredentialID, at date: Date = Date()) {
        preferences.lastUsed[id.key] = date
        persist()
    }

    // MARK: Saving after a sign-in

    /// Whether to offer saving `submission`; nil means stay quiet.
    ///
    /// Quiet when: the tab is private, offers are off, the site is on the never-save list,
    /// the password is empty, or the same password is already saved for this username
    /// (which counts as a use of that credential).
    public func offer(for submission: PasswordSubmission, isPrivate: Bool) -> SaveOffer? {
        guard !isPrivate, preferences.offerToSave, !submission.password.isEmpty,
              !isNeverSave(submission.origin) else { return nil }
        let saved = matches(for: submission.origin)
        let id = CredentialID(origin: submission.origin, username: submission.username)
        if saved.contains(where: { $0.id == id }) {
            if (try? store.password(for: id)) == submission.password {
                markUsed(id)
                return nil
            }
            return .update(id)
        }
        if submission.username.isEmpty {
            // A form without a username field: a change-password page for the only saved
            // login updates it; a password that is already saved is a sign-in with it.
            if let match = saved.first(where: { (try? store.password(for: $0.id)) == submission.password }) {
                markUsed(match.id)
                return nil
            }
            if submission.mode == .change, saved.count == 1 { return .update(saved[0].id) }
        }
        return .save
    }

    /// Carries out an accepted offer.
    @discardableResult
    public func accept(_ offer: SaveOffer, for submission: PasswordSubmission) throws -> SaveOutcome {
        switch offer {
        case .save:
            return try save(CredentialID(origin: submission.origin, username: submission.username),
                            password: submission.password)
        case .update(let id):
            try store.update(id, username: nil, password: submission.password)
            markUsed(id)
            reload()
            return .updated
        }
    }

    // MARK: Never-save list

    public func isNeverSave(_ origin: SiteOrigin) -> Bool { preferences.neverSave.contains(origin) }

    public func setNeverSave(_ never: Bool, for origin: SiteOrigin) {
        var list = Set(preferences.neverSave)
        if never { list.insert(origin) } else { list.remove(origin) }
        preferences.neverSave = list.sorted()
        persist()
    }

    // MARK: Preferences

    public func update(_ change: (inout PasswordPreferences) -> Void) {
        var copy = preferences
        change(&copy)
        guard copy != preferences else { return }
        preferences = copy
        persist()
    }

    private func persist() {
        do {
            try preferencesStore?.save(preferences)
        } catch {
            lastError = "Could not save password settings: \(error.localizedDescription)"
        }
    }

    // MARK: Import, export, audit

    public struct ImportSummary: Equatable, Sendable {
        public var added = 0
        public var updated = 0
        public var unchanged = 0
        public var failed = 0
        public var skipped: [PasswordCSV.Skipped] = []
    }

    public func importEntries(_ result: PasswordCSV.ImportResult) -> ImportSummary {
        var summary = ImportSummary(skipped: result.skipped)
        for entry in result.entries {
            do {
                switch try save(entry.id, password: entry.password, markUsed: false) {
                case .added: summary.added += 1
                case .updated: summary.updated += 1
                case .unchanged: summary.unchanged += 1
                }
            } catch {
                summary.failed += 1
            }
        }
        reload()
        return summary
    }

    /// Every saved credential with its password, for export. Reads each secret once.
    public func exportEntries() throws -> [PasswordCSV.Entry] {
        try credentials.map { PasswordCSV.Entry(origin: $0.origin, username: $0.username, password: try store.password(for: $0.id)) }
    }

    /// Weak and reused passwords. Reads every secret; the caller authenticates first.
    public func audit() -> PasswordAudit {
        var passwords: [CredentialID: String] = [:]
        for credential in credentials {
            if let password = try? store.password(for: credential.id) { passwords[credential.id] = password }
        }
        return PasswordAudit.run(passwords)
    }
}
