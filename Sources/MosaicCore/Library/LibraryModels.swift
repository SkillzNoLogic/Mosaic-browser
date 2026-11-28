import Foundation

/// A bookmark or a bookmark folder. Folders form a tree under two fixed roots,
/// `Bookmark.barID` (the bookmarks bar) and `Bookmark.otherID` (everything else).
public struct Bookmark: Identifiable, Hashable, Sendable {
    public enum Kind: String, Sendable { case url, folder }

    public static let barID = "bar"
    public static let otherID = "other"
    public static let rootIDs: Set<String> = [barID, otherID]

    public let id: String
    public var parentID: String?
    public var kind: Kind
    public var title: String
    /// Nil for folders.
    public var url: String?
    /// Order among its siblings, from 0.
    public var position: Int
    public var tags: [String]
    public var created: Date
    public var modified: Date

    public init(id: String = UUID().uuidString, parentID: String?, kind: Kind, title: String, url: String?,
                position: Int = 0, tags: [String] = [], created: Date = Date(), modified: Date = Date()) {
        self.id = id
        self.parentID = parentID
        self.kind = kind
        self.title = title
        self.url = url
        self.position = position
        self.tags = tags
        self.created = created
        self.modified = modified
    }

    public var isFolder: Bool { kind == .folder }
    public var isRoot: Bool { Self.rootIDs.contains(id) }

    /// The title, or the address when the bookmark has none.
    public var displayTitle: String {
        if !title.isEmpty { return title }
        if let url { return LibraryURL.display(url) }
        return "Untitled Folder"
    }
}

/// One address in history, with its aggregate counts.
public struct HistoryEntry: Identifiable, Hashable, Sendable {
    public let id: Int64
    public var url: String
    public var title: String
    public var host: String
    public var visitCount: Int
    public var typedCount: Int
    public var lastVisit: Date
    public var frecency: Double

    public init(id: Int64, url: String, title: String, host: String, visitCount: Int, typedCount: Int,
                lastVisit: Date, frecency: Double) {
        self.id = id
        self.url = url
        self.title = title
        self.host = host
        self.visitCount = visitCount
        self.typedCount = typedCount
        self.lastVisit = lastVisit
        self.frecency = frecency
    }

    public var displayTitle: String { title.isEmpty ? LibraryURL.display(url) : title }
}

/// One visit to an address (the History page lists these, newest first).
public struct HistoryVisit: Identifiable, Hashable, Sendable {
    public let id: Int64
    public let urlID: Int64
    public var url: String
    public var title: String
    public var host: String
    public var time: Date
    public var typed: Bool

    public var displayTitle: String { title.isEmpty ? LibraryURL.display(url) : title }
}

/// A page saved to read later.
public struct ReadingListItem: Identifiable, Hashable, Sendable {
    public let id: String
    public var url: String
    public var title: String
    public var added: Date
    /// When it was marked read, or nil while unread.
    public var readAt: Date?

    public var isRead: Bool { readAt != nil }
    public var displayTitle: String { title.isEmpty ? LibraryURL.display(url) : title }
}

/// A search engine the user added (name, address-bar keyword, URL template with `%s`).
public struct CustomSearchEngine: Identifiable, Hashable, Codable, Sendable {
    public var id: String
    public var name: String
    /// Typed before the terms in the address bar ("w swift" searches Wikipedia).
    public var keyword: String
    /// Search URL with `%s` standing in for the terms.
    public var template: String
    /// Optional OpenSearch suggestion URL with `%s`.
    public var suggestTemplate: String?

    public init(id: String = "custom." + UUID().uuidString, name: String, keyword: String, template: String,
                suggestTemplate: String? = nil) {
        self.id = id
        self.name = name
        self.keyword = keyword
        self.template = template
        self.suggestTemplate = suggestTemplate
    }

    /// The engine as a `SearchEngine`, so it can be the default.
    public var searchEngine: SearchEngine {
        let homepage = URL(string: template.replacingOccurrences(of: "%s", with: ""))
            .flatMap { url -> String? in
                guard let scheme = url.scheme, let host = url.host else { return nil }
                return "\(scheme)://\(host)/"
            } ?? template
        return SearchEngine(id: id, name: name, searchTemplate: template, homepage: homepage)
    }

    /// Why the template or keyword cannot be used, or nil when it is fine.
    public static func problem(name: String, keyword: String, template: String) -> String? {
        if name.trimmingCharacters(in: .whitespaces).isEmpty { return "Give the search engine a name." }
        if keyword.contains(where: \.isWhitespace) { return "A keyword cannot contain spaces." }
        guard template.contains("%s") else { return "The address must contain %s where the search terms go." }
        guard let url = URL(string: template.replacingOccurrences(of: "%s", with: "test")),
              let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme), url.host != nil
        else { return "The address must be a web address starting with https://." }
        return nil
    }
}

/// Address helpers shared by the store and the UI.
public enum LibraryURL {
    /// Only real web pages (and local files) go into history.
    public static func isRecordable(_ url: String) -> Bool {
        guard let scheme = URL(string: url)?.scheme?.lowercased() else { return false }
        return scheme == "http" || scheme == "https" || scheme == "file"
    }

    /// The host without a leading "www.", lower-cased; empty when there is none.
    public static func host(of url: String) -> String {
        guard let host = URL(string: url)?.host?.lowercased() else { return "" }
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }

    /// A short form for display: no scheme, no "www.", no trailing slash.
    public static func display(_ url: String) -> String {
        var text = url
        for prefix in ["https://", "http://"] where text.lowercased().hasPrefix(prefix) {
            text = String(text.dropFirst(prefix.count))
        }
        if text.lowercased().hasPrefix("www.") { text = String(text.dropFirst(4)) }
        if text.hasSuffix("/"), text.filter({ $0 == "/" }).count == 1 { text.removeLast() }
        return text
    }

    /// The form used to decide whether two addresses are "the same page" for the star:
    /// scheme and host lower-cased, fragment dropped, a bare trailing slash ignored.
    public static func normalized(_ url: String) -> String {
        guard var components = URLComponents(string: url) else { return url }
        components.fragment = nil
        components.scheme = components.scheme?.lowercased()
        components.host = components.host?.lowercased()
        if components.path == "/" { components.path = "" }
        return components.string ?? url
    }
}
