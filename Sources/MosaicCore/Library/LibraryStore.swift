import Foundation

/// Mosaic's library database: bookmarks (a folder tree with tags), history (addresses and
/// their visits), the reading list and custom search engines, in one SQLite file in the
/// profile folder.
///
/// Thread-safe: every call takes the store's lock, so it may be used from the main thread
/// for small lookups and from background queues for anything that scans history.
public final class LibraryStore: @unchecked Sendable {
    public static let fileName = "Library.sqlite"

    private let db: SQLiteDatabase
    private let lock = NSLock()

    /// Opens the database at `fileURL` (created and migrated as needed), or an in-memory
    /// database when nil.
    public init(fileURL: URL?) throws {
        db = try SQLiteDatabase(url: fileURL)
        try migrate()
    }

    private func locked<T>(_ body: (SQLiteDatabase) throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body(db)
    }

    // MARK: - Schema

    /// Each entry upgrades the database by one version; `user_version` records how far it got.
    /// Never edit a shipped step: append a new one.
    static let migrations: [String] = [
        // 1: the initial schema.
        """
        CREATE TABLE bookmarks (
          id         TEXT PRIMARY KEY,
          parent_id  TEXT REFERENCES bookmarks(id) ON DELETE CASCADE,
          kind       TEXT NOT NULL CHECK (kind IN ('url', 'folder')),
          title      TEXT NOT NULL DEFAULT '',
          url        TEXT,
          url_key    TEXT,
          position   INTEGER NOT NULL DEFAULT 0,
          created    INTEGER NOT NULL,
          modified   INTEGER NOT NULL
        );
        CREATE INDEX bookmarks_parent ON bookmarks(parent_id, position);
        CREATE INDEX bookmarks_url_key ON bookmarks(url_key);
        CREATE TABLE bookmark_tags (
          bookmark_id TEXT NOT NULL REFERENCES bookmarks(id) ON DELETE CASCADE,
          tag         TEXT NOT NULL COLLATE NOCASE,
          PRIMARY KEY (bookmark_id, tag)
        );
        CREATE INDEX bookmark_tags_tag ON bookmark_tags(tag);
        CREATE TABLE history_urls (
          id           INTEGER PRIMARY KEY,
          url          TEXT NOT NULL UNIQUE,
          title        TEXT NOT NULL DEFAULT '',
          host         TEXT NOT NULL DEFAULT '',
          visit_count  INTEGER NOT NULL DEFAULT 0,
          typed_count  INTEGER NOT NULL DEFAULT 0,
          last_visit   INTEGER NOT NULL,
          frecency     REAL NOT NULL DEFAULT 0
        );
        CREATE INDEX history_urls_frecency ON history_urls(frecency DESC);
        CREATE INDEX history_urls_last ON history_urls(last_visit DESC);
        CREATE INDEX history_urls_host ON history_urls(host);
        CREATE TABLE history_visits (
          id      INTEGER PRIMARY KEY,
          url_id  INTEGER NOT NULL REFERENCES history_urls(id) ON DELETE CASCADE,
          time    INTEGER NOT NULL,
          typed   INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX history_visits_time ON history_visits(time DESC);
        CREATE INDEX history_visits_url ON history_visits(url_id, time DESC);
        CREATE TABLE reading_list (
          id       TEXT PRIMARY KEY,
          url      TEXT NOT NULL UNIQUE,
          title    TEXT NOT NULL DEFAULT '',
          added    INTEGER NOT NULL,
          read_at  INTEGER
        );
        CREATE TABLE search_engines (
          id                TEXT PRIMARY KEY,
          name              TEXT NOT NULL,
          keyword           TEXT NOT NULL DEFAULT '',
          template          TEXT NOT NULL,
          suggest_template  TEXT,
          position          INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO bookmarks (id, parent_id, kind, title, position, created, modified)
          VALUES ('bar', NULL, 'folder', 'Bookmarks Bar', 0, strftime('%s','now') * 1000, strftime('%s','now') * 1000),
                 ('other', NULL, 'folder', 'Other Bookmarks', 1, strftime('%s','now') * 1000, strftime('%s','now') * 1000);
        INSERT INTO search_engines (id, name, keyword, template, suggest_template, position)
          VALUES ('custom.wikipedia', 'Wikipedia', 'w',
                  'https://en.wikipedia.org/wiki/Special:Search?search=%s',
                  'https://en.wikipedia.org/w/api.php?action=opensearch&limit=5&search=%s', 0);
        """,
    ]

    public static var schemaVersion: Int { migrations.count }

    private func migrate() throws {
        try locked { db in
            let current = db.userVersion
            guard current < Self.migrations.count else { return }
            try db.transaction {
                for step in Self.migrations[current...] { try db.execute(step) }
                try db.execute("PRAGMA user_version = \(Self.migrations.count)")
            }
        }
    }

    public var userVersion: Int { locked { $0.userVersion } }

    // MARK: - Bookmarks

    private static let bookmarkColumns = """
        b.id, b.parent_id, b.kind, b.title, b.url, b.position, b.created, b.modified,
        (SELECT group_concat(tag, char(31)) FROM bookmark_tags t WHERE t.bookmark_id = b.id)
        """

    private static func bookmark(_ row: SQLiteDatabase.Row) -> Bookmark {
        Bookmark(
            id: row.string(0), parentID: row.optionalString(1),
            kind: Bookmark.Kind(rawValue: row.string(2)) ?? .url,
            title: row.string(3), url: row.optionalString(4), position: Int(row.int(5)),
            tags: row.optionalString(8).map { $0.split(separator: "\u{1F}").map(String.init).sorted() } ?? [],
            created: Date(libraryMillis: row.int(6)), modified: Date(libraryMillis: row.int(7)))
    }

    /// Every bookmark and folder, ordered by parent then position.
    public func allBookmarks() -> [Bookmark] {
        locked { db in
            (try? db.query("SELECT \(Self.bookmarkColumns) FROM bookmarks b ORDER BY b.parent_id, b.position",
                           map: Self.bookmark)) ?? []
        }
    }

    public func children(of folderID: String) -> [Bookmark] {
        locked { db in
            (try? db.query("SELECT \(Self.bookmarkColumns) FROM bookmarks b WHERE b.parent_id = ? ORDER BY b.position",
                           .text(folderID), map: Self.bookmark)) ?? []
        }
    }

    public func bookmark(id: String) -> Bookmark? {
        locked { db in
            try? db.query("SELECT \(Self.bookmarkColumns) FROM bookmarks b WHERE b.id = ?", .text(id),
                          map: Self.bookmark).first
        }
    }

    /// Bookmarks of `url` (compared in normalised form), oldest first.
    public func bookmarks(forURL url: String) -> [Bookmark] {
        locked { db in
            (try? db.query("SELECT \(Self.bookmarkColumns) FROM bookmarks b WHERE b.url_key = ? ORDER BY b.created",
                           .text(LibraryURL.normalized(url)), map: Self.bookmark)) ?? []
        }
    }

    /// Normalised addresses of every bookmark (for the address bar's star).
    public func bookmarkedURLKeys() -> Set<String> {
        locked { db in
            Set((try? db.query("SELECT url_key FROM bookmarks WHERE url_key IS NOT NULL") { $0.string(0) }) ?? [])
        }
    }

    /// Bookmarks whose title, address or tags contain every word of `text`.
    public func searchBookmarks(_ text: String, limit: Int = 500) -> [Bookmark] {
        let terms = SuggestionRanker.terms(text)
        guard !terms.isEmpty else { return [] }
        var clauses: [String] = []
        var arguments: [SQLiteDatabase.Value] = []
        for term in terms {
            clauses.append("""
                (b.title LIKE ? ESCAPE '\\' OR b.url LIKE ? ESCAPE '\\'
                 OR EXISTS (SELECT 1 FROM bookmark_tags t WHERE t.bookmark_id = b.id AND t.tag LIKE ? ESCAPE '\\'))
                """)
            let pattern = Self.likePattern(term)
            arguments += [.text(pattern), .text(pattern), .text(pattern)]
        }
        arguments.append(.int(Int64(limit)))
        let sql = "SELECT \(Self.bookmarkColumns) FROM bookmarks b WHERE b.kind = 'url' AND "
            + clauses.joined(separator: " AND ") + " ORDER BY b.title COLLATE NOCASE LIMIT ?"
        return locked { db in (try? db.query(sql, arguments: arguments, map: Self.bookmark)) ?? [] }
    }

    /// Every tag in use, alphabetically.
    public func allTags() -> [String] {
        locked { db in
            (try? db.query("SELECT DISTINCT tag FROM bookmark_tags ORDER BY tag COLLATE NOCASE") { $0.string(0) }) ?? []
        }
    }

    /// Adds a bookmark at `position` in `parentID` (appended when nil).
    @discardableResult
    public func addBookmark(url: String, title: String, parentID: String = Bookmark.otherID, tags: [String] = [],
                            position: Int? = nil, created: Date = Date()) throws -> Bookmark {
        try insert(kind: .url, title: title, url: url, parentID: parentID, tags: tags, position: position,
                   created: created)
    }

    @discardableResult
    public func addFolder(title: String, parentID: String = Bookmark.otherID, position: Int? = nil,
                          created: Date = Date()) throws -> Bookmark {
        try insert(kind: .folder, title: title, url: nil, parentID: parentID, tags: [], position: position,
                   created: created)
    }

    private func insert(kind: Bookmark.Kind, title: String, url: String?, parentID: String, tags: [String],
                        position: Int?, created: Date) throws -> Bookmark {
        try locked { db in
            try db.transaction {
                try insertUnlocked(kind: kind, title: title, url: url, parentID: parentID, tags: tags,
                                   position: position, created: created, db)
            }
        }
    }

    /// Caller holds the lock and a transaction.
    private func insertUnlocked(kind: Bookmark.Kind, title: String, url: String?, parentID: String, tags: [String],
                                position: Int?, created: Date, _ db: SQLiteDatabase) throws -> Bookmark {
        guard try isFolder(parentID, db) else { throw SQLiteDatabase.Failure(description: "No such folder") }
        let count = Int(try db.scalar("SELECT count(*) FROM bookmarks WHERE parent_id = ?", .text(parentID)))
        let index = min(max(position ?? count, 0), count)
        if index < count {
            try db.run("UPDATE bookmarks SET position = position + 1 WHERE parent_id = ? AND position >= ?",
                       .text(parentID), .int(Int64(index)))
        }
        let bookmark = Bookmark(parentID: parentID, kind: kind, title: title, url: url, position: index,
                                tags: Self.cleanTags(tags), created: created, modified: created)
        try db.run("""
            INSERT INTO bookmarks (id, parent_id, kind, title, url, url_key, position, created, modified)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, .text(bookmark.id), .text(parentID), .text(kind.rawValue), .text(title),
            SQLiteDatabase.Value(url), SQLiteDatabase.Value(url.map(LibraryURL.normalized)),
            .int(Int64(index)), .int(created.libraryMillis), .int(created.libraryMillis))
        try writeTags(bookmark.tags, for: bookmark.id, db)
        return bookmark
    }

    /// Changes a bookmark's title, address and tags (any nil argument stays as it is).
    public func updateBookmark(id: String, title: String? = nil, url: String? = nil, tags: [String]? = nil) throws {
        try locked { db in
            try db.transaction {
                let now = Date().libraryMillis
                if let title {
                    try db.run("UPDATE bookmarks SET title = ?, modified = ? WHERE id = ?", .text(title), .int(now), .text(id))
                }
                if let url {
                    try db.run("UPDATE bookmarks SET url = ?, url_key = ?, modified = ? WHERE id = ? AND kind = 'url'",
                               .text(url), .text(LibraryURL.normalized(url)), .int(now), .text(id))
                }
                if let tags {
                    try db.run("DELETE FROM bookmark_tags WHERE bookmark_id = ?", .text(id))
                    try writeTags(Self.cleanTags(tags), for: id, db)
                    try db.run("UPDATE bookmarks SET modified = ? WHERE id = ?", .int(now), .text(id))
                }
            }
        }
    }

    /// Moves a bookmark or folder to `position` in `parentID` (the end when nil). A folder
    /// cannot move into itself or its own subfolders; the roots cannot move.
    public func moveBookmark(id: String, toParent parentID: String, position: Int? = nil) throws {
        guard !Bookmark.rootIDs.contains(id) else { throw SQLiteDatabase.Failure(description: "The root folders stay put") }
        try locked { db in
            try db.transaction {
                guard try isFolder(parentID, db) else { throw SQLiteDatabase.Failure(description: "No such folder") }
                // Walk up from the destination: meeting `id` means a folder into its own subtree.
                var cursor: String? = parentID
                while let current = cursor {
                    if current == id { throw SQLiteDatabase.Failure(description: "A folder cannot go inside itself") }
                    cursor = try db.query("SELECT parent_id FROM bookmarks WHERE id = ?", .text(current)) {
                        $0.optionalString(0)
                    }.first ?? nil
                }
                guard let oldParent = try db.query("SELECT parent_id FROM bookmarks WHERE id = ?", .text(id), map: {
                    $0.optionalString(0)
                }).first ?? nil else { throw SQLiteDatabase.Failure(description: "No such bookmark") }

                var siblings = try db.query(
                    "SELECT id FROM bookmarks WHERE parent_id = ? AND id != ? ORDER BY position",
                    .text(parentID), .text(id)) { $0.string(0) }
                let index = min(max(position ?? siblings.count, 0), siblings.count)
                siblings.insert(id, at: index)
                try db.run("UPDATE bookmarks SET parent_id = ?, modified = ? WHERE id = ?",
                           .text(parentID), .int(Date().libraryMillis), .text(id))
                try renumber(siblings, db)
                if oldParent != parentID { try compact(oldParent, db) }
            }
        }
    }

    /// Deletes a bookmark, or a folder with everything in it. The roots cannot be deleted.
    public func deleteBookmark(id: String) throws {
        guard !Bookmark.rootIDs.contains(id) else { return }
        try locked { db in
            try db.transaction {
                let parent = try db.query("SELECT parent_id FROM bookmarks WHERE id = ?", .text(id)) {
                    $0.optionalString(0)
                }.first ?? nil
                try db.run("DELETE FROM bookmarks WHERE id = ?", .text(id))
                if let parent { try compact(parent, db) }
            }
        }
    }

    /// Deletes every bookmark of `url`. Returns how many there were.
    @discardableResult
    public func removeBookmarks(forURL url: String) throws -> Int {
        let ids = bookmarks(forURL: url).map(\.id)
        for id in ids { try deleteBookmark(id: id) }
        return ids.count
    }

    public func bookmarkCount() -> Int {
        locked { db in Int((try? db.scalar("SELECT count(*) FROM bookmarks WHERE kind = 'url'")) ?? 0) }
    }

    private func isFolder(_ id: String, _ db: SQLiteDatabase) throws -> Bool {
        try db.scalar("SELECT count(*) FROM bookmarks WHERE id = ? AND kind = 'folder'", .text(id)) > 0
    }

    private func renumber(_ ids: [String], _ db: SQLiteDatabase) throws {
        for (index, id) in ids.enumerated() {
            try db.run("UPDATE bookmarks SET position = ? WHERE id = ?", .int(Int64(index)), .text(id))
        }
    }

    private func compact(_ parentID: String, _ db: SQLiteDatabase) throws {
        let ids = try db.query("SELECT id FROM bookmarks WHERE parent_id = ? ORDER BY position", .text(parentID)) {
            $0.string(0)
        }
        try renumber(ids, db)
    }

    private func writeTags(_ tags: [String], for id: String, _ db: SQLiteDatabase) throws {
        for tag in tags {
            try db.run("INSERT OR IGNORE INTO bookmark_tags (bookmark_id, tag) VALUES (?, ?)", .text(id), .text(tag))
        }
    }

    /// Trimmed, non-empty, without duplicates (ignoring case).
    public static func cleanTags(_ tags: [String]) -> [String] {
        var seen: Set<String> = []
        return tags.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty && seen.insert($0.lowercased()).inserted }
    }

    // MARK: - Importing a tree

    /// Adds bookmarks read from a file: the other browser's toolbar folder goes into the
    /// bookmarks bar, everything else into Other Bookmarks. Folders with the same name are
    /// merged and an address already in the same folder is skipped, so importing the same
    /// file twice adds nothing. Returns the number of bookmarks added.
    @discardableResult
    public func importBookmarks(_ items: [ImportedBookmark]) throws -> Int {
        try locked { db in
            try db.transaction {
                var added = 0
                for item in items {
                    if item.isToolbar {
                        added += try importUnlocked(item.children, into: Bookmark.barID, db)
                    } else {
                        added += try importUnlocked([item], into: Bookmark.otherID, db)
                    }
                }
                return added
            }
        }
    }

    /// Adds an imported tree under `parentID` (merging as `importBookmarks` does).
    @discardableResult
    public func importTree(_ items: [ImportedBookmark], into parentID: String) throws -> Int {
        try locked { db in
            try db.transaction { try importUnlocked(items, into: parentID, db) }
        }
    }

    private func importUnlocked(_ items: [ImportedBookmark], into parentID: String, _ db: SQLiteDatabase) throws -> Int {
        var added = 0
        for item in items {
            if item.isFolder {
                let existing = try db.query(
                    "SELECT id FROM bookmarks WHERE parent_id = ? AND kind = 'folder' AND title = ? LIMIT 1",
                    .text(parentID), .text(item.title)) { $0.string(0) }.first
                let folderID = try existing ?? insertUnlocked(
                    kind: .folder, title: item.title, url: nil, parentID: parentID, tags: [], position: nil,
                    created: item.added ?? Date(), db).id
                added += try importUnlocked(item.children, into: folderID, db)
            } else if let url = item.url, !url.isEmpty {
                let duplicate = try db.scalar(
                    "SELECT count(*) FROM bookmarks WHERE parent_id = ? AND url_key = ?",
                    .text(parentID), .text(LibraryURL.normalized(url))) > 0
                guard !duplicate else { continue }
                _ = try insertUnlocked(kind: .url, title: item.title, url: url, parentID: parentID, tags: item.tags,
                                       position: nil, created: item.added ?? Date(), db)
                added += 1
            }
        }
        return added
    }

    // MARK: - History

    private static let entryColumns = "id, url, title, host, visit_count, typed_count, last_visit, frecency"

    private static func entry(_ row: SQLiteDatabase.Row) -> HistoryEntry {
        HistoryEntry(id: row.int(0), url: row.string(1), title: row.string(2), host: row.string(3),
                     visitCount: Int(row.int(4)), typedCount: Int(row.int(5)),
                     lastVisit: Date(libraryMillis: row.int(6)), frecency: row.double(7))
    }

    /// Records one visit. `title` replaces the stored title when it is not empty.
    public func recordVisit(url: String, title: String, typed: Bool = false, at time: Date = Date()) throws {
        guard LibraryURL.isRecordable(url) else { return }
        try locked { db in
            try db.transaction {
                let millis = time.libraryMillis
                try db.run("""
                    INSERT INTO history_urls (url, title, host, visit_count, typed_count, last_visit)
                    VALUES (?1, ?2, ?3, 1, ?4, ?5)
                    ON CONFLICT(url) DO UPDATE SET
                      visit_count = visit_count + 1,
                      typed_count = typed_count + ?4,
                      title = CASE WHEN ?2 = '' THEN title ELSE ?2 END,
                      last_visit = max(last_visit, ?5)
                    """, .text(url), .text(title), .text(LibraryURL.host(of: url)), .int(typed ? 1 : 0), .int(millis))
                let urlID = try db.scalar("SELECT id FROM history_urls WHERE url = ?", .text(url))
                try db.run("INSERT INTO history_visits (url_id, time, typed) VALUES (?, ?, ?)",
                           .int(urlID), .int(millis), .int(typed ? 1 : 0))
                try updateFrecency(urlID, now: Date(), db)
            }
        }
    }

    /// A page's title arrived (or changed) after its visit was recorded.
    public func updateTitle(url: String, title: String) {
        guard !title.isEmpty else { return }
        locked { db in _ = try? db.run("UPDATE history_urls SET title = ? WHERE url = ?", .text(title), .text(url)) }
    }

    private func updateFrecency(_ urlID: Int64, now: Date, _ db: SQLiteDatabase) throws {
        let visits = try db.query(
            "SELECT time, typed FROM history_visits WHERE url_id = ? ORDER BY time DESC LIMIT ?",
            .int(urlID), .int(Int64(Frecency.sampleSize))) { (time: Date(libraryMillis: $0.int(0)), typed: $0.bool(1)) }
        let count = Int(try db.scalar("SELECT visit_count FROM history_urls WHERE id = ?", .int(urlID)))
        try db.run("UPDATE history_urls SET frecency = ? WHERE id = ?",
                   .real(Frecency.score(visitCount: count, visits: visits, now: now)), .int(urlID))
    }

    /// Scores decay with time even without new visits; recompute them all (in the
    /// background, about once a day).
    public func recomputeFrecency(now: Date = Date()) {
        let ids = locked { db in (try? db.query("SELECT id FROM history_urls") { $0.int(0) }) ?? [] }
        // In slices, so a large history never holds the lock long enough to stall typing.
        for slice in stride(from: 0, to: ids.count, by: 200) {
            locked { db in
                _ = try? db.transaction {
                    for id in ids[slice..<min(slice + 200, ids.count)] { try updateFrecency(id, now: now, db) }
                }
            }
        }
        setMeta("frecencyComputed", String(now.libraryMillis))
    }

    /// When `recomputeFrecency` last ran.
    public var frecencyComputed: Date? {
        meta("frecencyComputed").flatMap(Int64.init).map(Date.init(libraryMillis:))
    }

    /// Addresses by frecency, best first (the address bar's in-memory index).
    public func topHistory(limit: Int) -> [HistoryEntry] {
        locked { db in
            (try? db.query("SELECT \(Self.entryColumns) FROM history_urls WHERE frecency > 0 ORDER BY frecency DESC LIMIT ?",
                           .int(Int64(limit)), map: Self.entry)) ?? []
        }
    }

    /// Addresses whose address or title contains every word of `text`, best first.
    /// Scans the whole table: call it off the main thread.
    public func searchHistory(_ text: String, limit: Int) -> [HistoryEntry] {
        let terms = SuggestionRanker.terms(text)
        guard !terms.isEmpty else { return [] }
        let clauses = terms.map { _ in "(url LIKE ? ESCAPE '\\' OR title LIKE ? ESCAPE '\\')" }
        var arguments: [SQLiteDatabase.Value] = terms.flatMap { [.text(Self.likePattern($0)), .text(Self.likePattern($0))] }
        arguments.append(.int(Int64(limit)))
        let sql = "SELECT \(Self.entryColumns) FROM history_urls WHERE " + clauses.joined(separator: " AND ")
            + " ORDER BY frecency DESC LIMIT ?"
        return locked { db in (try? db.query(sql, arguments: arguments, map: Self.entry)) ?? [] }
    }

    /// The most visited sites, one address per site.
    public func topSites(limit: Int) -> [HistoryEntry] {
        var seen: Set<String> = []
        return topHistory(limit: max(limit * 12, 100)).filter { entry in
            let key = entry.host.isEmpty ? entry.url : entry.host
            return seen.insert(key).inserted
        }.prefix(limit).map { $0 }
    }

    /// Most recently visited addresses (the History menu).
    public func recentHistory(limit: Int) -> [HistoryEntry] {
        locked { db in
            (try? db.query("SELECT \(Self.entryColumns) FROM history_urls ORDER BY last_visit DESC LIMIT ?",
                           .int(Int64(limit)), map: Self.entry)) ?? []
        }
    }

    public func historyEntry(url: String) -> HistoryEntry? {
        locked { db in
            try? db.query("SELECT \(Self.entryColumns) FROM history_urls WHERE url = ?", .text(url), map: Self.entry).first
        }
    }

    /// Visits newest first, optionally filtered (every word in the address or title) and
    /// only those before `before` (for paging).
    public func visits(matching text: String = "", before: Date? = nil, limit: Int = 500) -> [HistoryVisit] {
        var clauses: [String] = []
        var arguments: [SQLiteDatabase.Value] = []
        for term in SuggestionRanker.terms(text) {
            clauses.append("(u.url LIKE ? ESCAPE '\\' OR u.title LIKE ? ESCAPE '\\')")
            arguments += [.text(Self.likePattern(term)), .text(Self.likePattern(term))]
        }
        if let before {
            clauses.append("v.time < ?")
            arguments.append(.int(before.libraryMillis))
        }
        arguments.append(.int(Int64(limit)))
        let sql = """
            SELECT v.id, u.id, u.url, u.title, u.host, v.time, v.typed
            FROM history_visits v JOIN history_urls u ON u.id = v.url_id
            \(clauses.isEmpty ? "" : "WHERE " + clauses.joined(separator: " AND "))
            ORDER BY v.time DESC LIMIT ?
            """
        return locked { db in
            (try? db.query(sql, arguments: arguments) { row in
                HistoryVisit(id: row.int(0), urlID: row.int(1), url: row.string(2), title: row.string(3),
                             host: row.string(4), time: Date(libraryMillis: row.int(5)), typed: row.bool(6))
            }) ?? []
        }
    }

    public func historyCounts() -> (addresses: Int, visits: Int) {
        locked { db in
            (Int((try? db.scalar("SELECT count(*) FROM history_urls")) ?? 0),
             Int((try? db.scalar("SELECT count(*) FROM history_visits")) ?? 0))
        }
    }

    /// Deletes single visits (the address goes too once it has none left).
    public func deleteVisits(ids: [Int64]) throws {
        try locked { db in
            try db.transaction {
                var urlIDs: Set<Int64> = []
                for id in ids {
                    if let urlID = try db.query("SELECT url_id FROM history_visits WHERE id = ?", .int(id), map: { $0.int(0) }).first {
                        urlIDs.insert(urlID)
                        try db.run("DELETE FROM history_visits WHERE id = ?", .int(id))
                    }
                }
                for urlID in urlIDs { try refreshAggregates(urlID, db) }
            }
        }
    }

    /// Deletes an address and all its visits.
    public func deleteHistory(url: String) throws {
        try locked { db in _ = try db.run("DELETE FROM history_urls WHERE url = ?", .text(url)) }
    }

    /// Deletes everything from a site (`example.com` also covers its subdomains).
    /// Returns how many addresses went.
    @discardableResult
    public func deleteHistory(site: String) throws -> Int {
        let host = LibraryURL.host(of: "https://" + site.lowercased())
        guard !host.isEmpty else { return 0 }
        return try locked { db in
            try db.run("DELETE FROM history_urls WHERE host = ? OR host LIKE ? ESCAPE '\\'",
                       .text(host), .text("%." + Self.escapeLike(host)))
        }
    }

    /// Deletes visits made at or after `since` (everything when nil).
    public func clearHistory(since: Date?) throws {
        try locked { db in
            try db.transaction {
                guard let since else {
                    try db.execute("DELETE FROM history_visits; DELETE FROM history_urls;")
                    return
                }
                let cutoff = since.libraryMillis
                let affected = try db.query("SELECT DISTINCT url_id FROM history_visits WHERE time >= ?", .int(cutoff)) {
                    $0.int(0)
                }
                try db.run("DELETE FROM history_visits WHERE time >= ?", .int(cutoff))
                for urlID in affected { try refreshAggregates(urlID, db) }
            }
        }
    }

    /// Re-derives an address's counts from its remaining visits; deletes it when none remain.
    private func refreshAggregates(_ urlID: Int64, _ db: SQLiteDatabase) throws {
        let count = try db.scalar("SELECT count(*) FROM history_visits WHERE url_id = ?", .int(urlID))
        guard count > 0 else {
            try db.run("DELETE FROM history_urls WHERE id = ?", .int(urlID))
            return
        }
        try db.run("""
            UPDATE history_urls SET
              visit_count = ?2,
              typed_count = (SELECT count(*) FROM history_visits WHERE url_id = ?1 AND typed = 1),
              last_visit = (SELECT max(time) FROM history_visits WHERE url_id = ?1)
            WHERE id = ?1
            """, .int(urlID), .int(count))
        try updateFrecency(urlID, now: Date(), db)
    }

    // MARK: - Reading list

    private static func readingItem(_ row: SQLiteDatabase.Row) -> ReadingListItem {
        ReadingListItem(id: row.string(0), url: row.string(1), title: row.string(2),
                        added: Date(libraryMillis: row.int(3)),
                        readAt: row.isNull(4) ? nil : Date(libraryMillis: row.int(4)))
    }

    /// Unread first, then newest first.
    public func readingList() -> [ReadingListItem] {
        locked { db in
            (try? db.query("""
                SELECT id, url, title, added, read_at FROM reading_list
                ORDER BY read_at IS NOT NULL, added DESC
                """, map: Self.readingItem)) ?? []
        }
    }

    /// Adds a page (or, if it is already there, marks it unread again and moves it to the top).
    @discardableResult
    public func addToReadingList(url: String, title: String, at time: Date = Date()) throws -> ReadingListItem {
        try locked { db in
            try db.run("""
                INSERT INTO reading_list (id, url, title, added) VALUES (?1, ?2, ?3, ?4)
                ON CONFLICT(url) DO UPDATE SET read_at = NULL, added = ?4,
                  title = CASE WHEN ?3 = '' THEN title ELSE ?3 END
                """, .text(UUID().uuidString), .text(url), .text(title), .int(time.libraryMillis))
            return try db.query("SELECT id, url, title, added, read_at FROM reading_list WHERE url = ?", .text(url),
                                map: Self.readingItem)[0]
        }
    }

    public func setRead(id: String, read: Bool) throws {
        try locked { db in
            _ = try db.run("UPDATE reading_list SET read_at = ? WHERE id = ?",
                           read ? .int(Date().libraryMillis) : .null, .text(id))
        }
    }

    public func removeFromReadingList(id: String) throws {
        try locked { db in _ = try db.run("DELETE FROM reading_list WHERE id = ?", .text(id)) }
    }

    public func isInReadingList(_ url: String) -> Bool {
        locked { db in ((try? db.scalar("SELECT count(*) FROM reading_list WHERE url = ?", .text(url))) ?? 0) > 0 }
    }

    // MARK: - Custom search engines

    public func customEngines() -> [CustomSearchEngine] {
        locked { db in
            (try? db.query("SELECT id, name, keyword, template, suggest_template FROM search_engines ORDER BY position, name") { row in
                CustomSearchEngine(id: row.string(0), name: row.string(1), keyword: row.string(2),
                                   template: row.string(3), suggestTemplate: row.optionalString(4))
            }) ?? []
        }
    }

    /// Adds or replaces an engine (matched by id).
    public func saveCustomEngine(_ engine: CustomSearchEngine) throws {
        try locked { db in
            let position = try db.scalar("SELECT coalesce(max(position) + 1, 0) FROM search_engines")
            try db.run("""
                INSERT INTO search_engines (id, name, keyword, template, suggest_template, position)
                VALUES (?, ?, ?, ?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, keyword = excluded.keyword,
                  template = excluded.template, suggest_template = excluded.suggest_template
                """, .text(engine.id), .text(engine.name), .text(engine.keyword), .text(engine.template),
                SQLiteDatabase.Value(engine.suggestTemplate), .int(position))
        }
    }

    public func deleteCustomEngine(id: String) throws {
        try locked { db in _ = try db.run("DELETE FROM search_engines WHERE id = ?", .text(id)) }
    }

    // MARK: - Helpers

    private func meta(_ key: String) -> String? {
        locked { db in try? db.query("SELECT value FROM meta WHERE key = ?", .text(key)) { $0.string(0) }.first }
    }

    private func setMeta(_ key: String, _ value: String) {
        locked { db in
            _ = try? db.run("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                            .text(key), .text(value))
        }
    }

    static func escapeLike(_ text: String) -> String {
        text.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "%", with: "\\%")
            .replacingOccurrences(of: "_", with: "\\_")
    }

    static func likePattern(_ term: String) -> String { "%" + escapeLike(term) + "%" }
}
