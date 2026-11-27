import Foundation

/// A bookmark or folder read from another browser's file, before it goes into the store.
public struct ImportedBookmark: Hashable, Sendable {
    public var title: String
    /// Nil for folders.
    public var url: String?
    public var added: Date?
    public var tags: [String]
    /// The other browser's bookmarks bar / toolbar folder: its contents go to Mosaic's bar.
    public var isToolbar: Bool
    public var children: [ImportedBookmark]

    public init(title: String, url: String? = nil, added: Date? = nil, tags: [String] = [], isToolbar: Bool = false,
                children: [ImportedBookmark] = []) {
        self.title = title
        self.url = url
        self.added = added
        self.tags = tags
        self.isToolbar = isToolbar
        self.children = children
    }

    public var isFolder: Bool { url == nil }

    /// Bookmarks (not folders) in this subtree.
    public var bookmarkCount: Int { isFolder ? children.reduce(0) { $0 + $1.bookmarkCount } : 1 }
}

// MARK: - Netscape bookmark HTML

/// The Netscape bookmark file format (`<!DOCTYPE NETSCAPE-Bookmark-file-1>`) that every
/// browser imports and exports.
public enum NetscapeBookmarks {
    /// Reads a bookmark file. Tolerant of the many dialects browsers write (missing `<p>`,
    /// unclosed `<DT>`, upper or lower case).
    public static func parse(_ html: String) -> [ImportedBookmark] {
        final class Node {
            var item: ImportedBookmark
            var children: [Node] = []
            init(_ item: ImportedBookmark) { self.item = item }
            func build() -> ImportedBookmark {
                var result = item
                result.children = children.map { $0.build() }
                return result
            }
        }

        let root = Node(ImportedBookmark(title: ""))
        var stack: [Node] = []
        var lastFolder: Node?
        var capture: (node: Node, closing: String)?
        var text = ""

        var index = html.startIndex
        while index < html.endIndex {
            guard let open = html[index...].firstIndex(of: "<") else {
                if capture != nil { text += html[index...] }
                break
            }
            if capture != nil { text += html[index..<open] }
            guard let close = html[open...].firstIndex(of: ">") else { break }
            let tag = String(html[html.index(after: open)..<close])
            index = html.index(after: close)

            let name = tag.prefix { !$0.isWhitespace }.lowercased()
            if let current = capture {
                if name == current.closing {
                    current.node.item.title = decodeEntities(text).trimmingCharacters(in: .whitespacesAndNewlines)
                    capture = nil
                }
                continue
            }
            let parent = stack.last ?? root
            switch name {
            case "h3":
                let attributes = parseAttributes(tag)
                let folder = Node(ImportedBookmark(
                    title: "", added: attributes["add_date"].flatMap(date),
                    isToolbar: attributes["personal_toolbar_folder"]?.lowercased() == "true"))
                parent.children.append(folder)
                lastFolder = folder
                capture = (folder, "/h3")
                text = ""
            case "a":
                let attributes = parseAttributes(tag)
                guard let href = attributes["href"], !href.isEmpty else { continue }
                let tags = (attributes["tags"] ?? "").split(separator: ",").map {
                    $0.trimmingCharacters(in: .whitespaces)
                }.filter { !$0.isEmpty }
                let link = Node(ImportedBookmark(
                    title: "", url: href, added: attributes["add_date"].flatMap(date), tags: tags))
                parent.children.append(link)
                lastFolder = nil
                capture = (link, "/a")
                text = ""
            case "dl":
                // A list opens the folder whose heading came just before it; the first list is the root.
                if let folder = lastFolder {
                    stack.append(folder)
                } else if stack.isEmpty {
                    stack.append(root)
                } else {
                    stack.append(parent)
                }
                lastFolder = nil
            case "/dl":
                if !stack.isEmpty { stack.removeLast() }
                lastFolder = nil
            default:
                break
            }
        }
        return root.children.map { $0.build() }
    }

    /// Writes a bookmark file: the bar as the toolbar folder, then everything in Other
    /// Bookmarks at the top level (the layout Chrome and Safari use).
    public static func export(_ bookmarks: [Bookmark]) -> String {
        let byParent = Dictionary(grouping: bookmarks.filter { $0.parentID != nil }, by: { $0.parentID! })
            .mapValues { $0.sorted { $0.position < $1.position } }
        var lines = [
            "<!DOCTYPE NETSCAPE-Bookmark-file-1>",
            "<!-- This is an automatically generated file.",
            "     It will be read and overwritten.",
            "     DO NOT EDIT! -->",
            "<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">",
            "<TITLE>Bookmarks</TITLE>",
            "<H1>Bookmarks</H1>",
            "<DL><p>",
        ]
        func write(_ items: [Bookmark], depth: Int) {
            let indent = String(repeating: "    ", count: depth)
            for item in items {
                let added = "ADD_DATE=\"\(Int(item.created.timeIntervalSince1970))\""
                if item.isFolder {
                    let modified = "LAST_MODIFIED=\"\(Int(item.modified.timeIntervalSince1970))\""
                    let toolbar = item.id == Bookmark.barID ? " PERSONAL_TOOLBAR_FOLDER=\"true\"" : ""
                    lines.append("\(indent)<DT><H3 \(added) \(modified)\(toolbar)>\(escape(item.title))</H3>")
                    lines.append("\(indent)<DL><p>")
                    write(byParent[item.id] ?? [], depth: depth + 1)
                    lines.append("\(indent)</DL><p>")
                } else if let url = item.url {
                    let tags = item.tags.isEmpty ? "" : " TAGS=\"\(escape(item.tags.joined(separator: ",")))\""
                    lines.append("\(indent)<DT><A HREF=\"\(escape(url))\" \(added)\(tags)>\(escape(item.title))</A>")
                }
            }
        }
        if let bar = bookmarks.first(where: { $0.id == Bookmark.barID }) { write([bar], depth: 1) }
        write(byParent[Bookmark.otherID] ?? [], depth: 1)
        lines.append("</DL><p>")
        return lines.joined(separator: "\n") + "\n"
    }

    static func parseAttributes(_ tag: String) -> [String: String] {
        var attributes: [String: String] = [:]
        let scalars = Array(tag)
        var i = scalars.firstIndex(where: \.isWhitespace) ?? scalars.count
        while i < scalars.count {
            while i < scalars.count, scalars[i].isWhitespace { i += 1 }
            let nameStart = i
            while i < scalars.count, !scalars[i].isWhitespace, scalars[i] != "=" { i += 1 }
            let name = String(scalars[nameStart..<i]).lowercased()
            while i < scalars.count, scalars[i].isWhitespace { i += 1 }
            guard i < scalars.count, scalars[i] == "=" else {
                if !name.isEmpty { attributes[name] = "" }
                continue
            }
            i += 1
            while i < scalars.count, scalars[i].isWhitespace { i += 1 }
            var value = ""
            if i < scalars.count, scalars[i] == "\"" || scalars[i] == "'" {
                let quote = scalars[i]
                i += 1
                let start = i
                while i < scalars.count, scalars[i] != quote { i += 1 }
                value = String(scalars[start..<min(i, scalars.count)])
                i += 1
            } else {
                let start = i
                while i < scalars.count, !scalars[i].isWhitespace { i += 1 }
                value = String(scalars[start..<i])
            }
            if !name.isEmpty { attributes[name] = decodeEntities(value) }
        }
        return attributes
    }

    private static func date(_ text: String) -> Date? {
        guard let seconds = Double(text), seconds > 0 else { return nil }
        // Some exporters write microseconds or milliseconds.
        if seconds > 1e14 { return Date(timeIntervalSince1970: seconds / 1_000_000) }
        if seconds > 1e11 { return Date(timeIntervalSince1970: seconds / 1000) }
        return Date(timeIntervalSince1970: seconds)
    }

    static func escape(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
    }

    static func decodeEntities(_ text: String) -> String {
        guard text.contains("&") else { return text }
        var result = ""
        var index = text.startIndex
        while index < text.endIndex {
            let character = text[index]
            guard character == "&", let semicolon = text[index...].prefix(12).firstIndex(of: ";") else {
                result.append(character)
                index = text.index(after: index)
                continue
            }
            let entity = text[text.index(after: index)..<semicolon]
            var decoded: String?
            switch entity {
            case "amp": decoded = "&"
            case "lt": decoded = "<"
            case "gt": decoded = ">"
            case "quot": decoded = "\""
            case "apos": decoded = "'"
            case "nbsp": decoded = "\u{A0}"
            default:
                if entity.hasPrefix("#x") || entity.hasPrefix("#X"), let code = UInt32(entity.dropFirst(2), radix: 16) {
                    decoded = Unicode.Scalar(code).map { String(Character($0)) }
                } else if entity.hasPrefix("#"), let code = UInt32(entity.dropFirst()) {
                    decoded = Unicode.Scalar(code).map { String(Character($0)) }
                }
            }
            if let decoded {
                result += decoded
                index = text.index(after: semicolon)
            } else {
                result.append(character)
                index = text.index(after: index)
            }
        }
        return result
    }
}

// MARK: - Chrome's Bookmarks file

/// Chrome (and Chromium-family browsers) keep bookmarks in a JSON file named `Bookmarks`
/// in the profile folder.
public enum ChromeBookmarks {
    /// Where Chrome's default profile keeps them.
    public static var defaultFile: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Google/Chrome/Default/Bookmarks")
    }

    public static func parse(_ data: Data) -> [ImportedBookmark]? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let roots = json["roots"] as? [String: Any] else { return nil }
        var result: [ImportedBookmark] = []
        if let bar = roots["bookmark_bar"] as? [String: Any] {
            var folder = node(bar) ?? ImportedBookmark(title: "Bookmarks Bar")
            folder.isToolbar = true
            result.append(folder)
        }
        if let other = roots["other"] as? [String: Any], let folder = node(other) {
            result += folder.children
        }
        if let synced = roots["synced"] as? [String: Any], var folder = node(synced), !folder.children.isEmpty {
            if folder.title.isEmpty { folder.title = "Mobile Bookmarks" }
            result.append(folder)
        }
        return result
    }

    private static func node(_ json: [String: Any]) -> ImportedBookmark? {
        let title = json["name"] as? String ?? ""
        // Chrome counts microseconds since 1601-01-01.
        let added = (json["date_added"] as? String).flatMap(Double.init).flatMap { value -> Date? in
            value > 0 ? Date(timeIntervalSince1970: value / 1_000_000 - 11_644_473_600) : nil
        }
        switch json["type"] as? String {
        case "url":
            guard let url = json["url"] as? String else { return nil }
            return ImportedBookmark(title: title, url: url, added: added)
        case "folder":
            let children = (json["children"] as? [[String: Any]] ?? []).compactMap(node)
            return ImportedBookmark(title: title, added: added, children: children)
        default:
            return nil
        }
    }
}
