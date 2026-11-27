import Foundation

/// A message from Mosaic's password page script (`Resources/gecko/passwords-page.js`), in
/// either engine.
///
/// The page script supplies `kind` and the page fields. The *engine side* supplies `origin`,
/// `frame`, `tab` and `isPrivate` from what it knows about the frame (Gecko: the parent-process
/// actor reads the document principal; Chromium: the bridge checks the frame a per-frame secret
/// token was given to). A page can therefore never make Mosaic file a password under another
/// site's origin.
///
/// Decoding never fails: unknown or malformed fields are left nil. This matters because the
/// message travels inside Gecko's state snapshot, and one bad event must not drop a snapshot.
public struct PasswordPageMessage: Decodable, Equatable, Sendable {
    public enum Kind: String, Sendable {
        /// The document's login state: sent when the page loads and when a login form appears.
        case page
        /// A form with a password was submitted (or a script-driven sign-in finished).
        case submit
        /// A new-password field got focus: offer a generated password.
        case generate
        /// The user pressed the fill shortcut inside the page (Gecko, which has the keyboard).
        case fillShortcut
        /// Result of a fill request: `count` fields filled.
        case filled
        /// A form with only a username was submitted (step one of a two-step sign-in).
        case identifier
    }

    public enum Frame: String, Sendable {
        /// The tab's top-level document.
        case main
        /// A subframe with the same origin as the top-level document.
        case sameOrigin = "same"
        /// A subframe from another origin. Never filled; never offered for saving.
        case crossOrigin = "cross"
    }

    public var kind: Kind?
    /// Random per-document identifier from the page script; tells a new document from the same one.
    public var document: String?
    public var hasLogin: Bool?
    public var hasNewPassword: Bool?
    public var username: String?
    public var password: String?
    public var mode: PasswordSubmission.Mode?
    /// For `submit`: the page script saw the sign-in finish (form gone, address changed).
    public var confirmed: Bool?
    public var count: Int?

    // Set by the engine side.
    public var origin: SiteOrigin?
    public var frame: Frame?
    /// Gecko: the agent's tab ID.
    public var tab: Int?
    /// Gecko: the browsing context is private.
    public var isPrivate: Bool?

    public init(kind: Kind? = nil) {
        self.kind = kind
    }

    enum CodingKeys: String, CodingKey {
        case kind, document = "doc", hasLogin = "login", hasNewPassword = "signup", username, password, mode
        case confirmed, count, origin, frame, tab, isPrivate = "private"
    }

    public init(from decoder: Decoder) throws {
        guard let c = try? decoder.container(keyedBy: CodingKeys.self) else { return }
        kind = (try? c.decode(String.self, forKey: .kind)).flatMap(Kind.init(rawValue:))
        document = try? c.decode(String.self, forKey: .document)
        hasLogin = try? c.decode(Bool.self, forKey: .hasLogin)
        hasNewPassword = try? c.decode(Bool.self, forKey: .hasNewPassword)
        username = (try? c.decode(String.self, forKey: .username)).map { String($0.prefix(Self.maxUsername)) }
        // An implausibly long "password" is dropped rather than truncated into a wrong one.
        password = (try? c.decode(String.self, forKey: .password)).flatMap { $0.count <= Self.maxPassword ? $0 : nil }
        mode = (try? c.decode(String.self, forKey: .mode)).flatMap(PasswordSubmission.Mode.init(rawValue:))
        confirmed = try? c.decode(Bool.self, forKey: .confirmed)
        count = try? c.decode(Int.self, forKey: .count)
        origin = (try? c.decode(String.self, forKey: .origin)).flatMap { SiteOrigin(url: $0) }
        frame = (try? c.decode(String.self, forKey: .frame)).flatMap(Frame.init(rawValue:))
        tab = try? c.decode(Int.self, forKey: .tab)
        isPrivate = try? c.decode(Bool.self, forKey: .isPrivate)
    }

    /// Decodes a page script's JSON text (Chromium path). Nil if it is not a JSON object.
    public static func decode(json: String) -> PasswordPageMessage? {
        guard let data = json.data(using: .utf8),
              (try? JSONSerialization.jsonObject(with: data)) is [String: Any]
        else { return nil }
        return try? JSONDecoder().decode(PasswordPageMessage.self, from: data)
    }

    static let maxUsername = 512
    static let maxPassword = 1024

    /// The submission this message describes, if it is a usable one: a `submit` with a
    /// password, from the top-level document or a same-origin frame, with a known origin.
    public var submission: PasswordSubmission? {
        guard kind == .submit, let origin, let password, !password.isEmpty,
              frame == .main || frame == .sameOrigin else { return nil }
        return PasswordSubmission(origin: origin,
                                  username: (username ?? "").trimmingCharacters(in: .whitespacesAndNewlines),
                                  password: password, mode: mode ?? .login)
    }
}
