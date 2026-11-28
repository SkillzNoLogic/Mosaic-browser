using Microsoft.Data.Sqlite;

namespace Mosaic.Core.Library;

/// <summary>
/// Mosaic's library database: bookmarks (a folder tree with tags), history (addresses and
/// their visits), the reading list and custom search engines, in one SQLite file in the
/// profile folder (<see cref="FileName"/>, the same schema as macOS).
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Library/LibraryStore.swift</c>. Thread-safe: every call takes
/// the store's lock. Every call does database work, so call it off the UI thread
/// (<see cref="BrowserLibrary"/> does).
/// </remarks>
public sealed class LibraryStore : IDisposable
{
    public const string FileName = "Library.sqlite";

    private readonly SqliteDatabase db;
    private readonly Lock gate = new();
    private readonly TimeProvider time;

    /// <summary>
    /// Opens the database at <paramref name="path"/> (created and migrated as needed), or an
    /// in-memory database when null.
    /// </summary>
    /// <exception cref="LibraryException">The file cannot be opened or migrated.</exception>
    public LibraryStore(string? path, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        db = new SqliteDatabase(path);
        try
        {
            Migrate();
        }
        catch (SqliteException error)
        {
            db.Dispose();
            throw new LibraryException($"Could not prepare the library database: {error.Message}", error);
        }
    }

    public void Dispose()
    {
        lock (gate) db.Dispose();
    }

    private T Locked<T>(Func<SqliteDatabase, T> body)
    {
        lock (gate) return body(db);
    }

    private void Locked(Action<SqliteDatabase> body)
    {
        lock (gate) body(db);
    }

    /// <summary>Reads swallow database errors (as <c>try?</c> does on macOS): a broken read shows nothing.</summary>
    private T Read<T>(Func<SqliteDatabase, T> body, T fallback)
    {
        lock (gate)
        {
            try { return body(db); }
            catch (SqliteException) { return fallback; }
        }
    }

    private DateTimeOffset Now => time.GetUtcNow();

    // ---- Schema ----

    /// <summary>
    /// Each entry upgrades the database by one version; <c>user_version</c> records how far it
    /// got. Never edit a shipped step: append a new one. Identical to the macOS steps, so the
    /// file moves between platforms.
    /// </summary>
    internal static readonly string[] Migrations =
    [
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
    ];

    public static int SchemaVersion => Migrations.Length;

    private void Migrate() => Locked(db =>
    {
        var current = db.UserVersion;
        if (current >= Migrations.Length) return;
        db.Transaction(() =>
        {
            foreach (var step in Migrations[current..]) db.Execute(step);
            db.UserVersion = Migrations.Length;
        });
    });

    public int UserVersion => Locked(db => db.UserVersion);

    // ---- Bookmarks ----

    private const string BookmarkColumns = """
        b.id, b.parent_id, b.kind, b.title, b.url, b.position, b.created, b.modified,
        (SELECT group_concat(tag, char(31)) FROM bookmark_tags t WHERE t.bookmark_id = b.id)
        """;

    private static Bookmark ReadBookmark(SqliteDatabase.Row row) => new()
    {
        Id = row.String(0),
        ParentId = row.OptionalString(1),
        Kind = Library.Bookmark.KindFrom(row.String(2)),
        Title = row.String(3),
        Url = row.OptionalString(4),
        Position = (int)row.Int(5),
        Tags = row.OptionalString(8) is { } tags ? [.. tags.Split('\u001f').Order(StringComparer.Ordinal)] : [],
        Created = row.Time(6),
        Modified = row.Time(7),
    };

    /// <summary>Every bookmark and folder, ordered by parent then position.</summary>
    public IReadOnlyList<Bookmark> AllBookmarks() =>
        Read(db => db.Query($"SELECT {BookmarkColumns} FROM bookmarks b ORDER BY b.parent_id, b.position", ReadBookmark), []);

    public IReadOnlyList<Bookmark> Children(string folderId) =>
        Read(db => db.Query($"SELECT {BookmarkColumns} FROM bookmarks b WHERE b.parent_id = ? ORDER BY b.position", ReadBookmark, folderId), []);

    public Bookmark? Bookmark(string id) =>
        Read(db => db.Query($"SELECT {BookmarkColumns} FROM bookmarks b WHERE b.id = ?", ReadBookmark, id).FirstOrDefault(), null);

    /// <summary>Bookmarks of <paramref name="url"/> (compared in normalised form), oldest first.</summary>
    public IReadOnlyList<Bookmark> BookmarksForUrl(string url) =>
        Read(db => db.Query($"SELECT {BookmarkColumns} FROM bookmarks b WHERE b.url_key = ? ORDER BY b.created", ReadBookmark,
            LibraryUrl.Normalized(url)), []);

    /// <summary>Normalised addresses of every bookmark (for the address bar's star).</summary>
    public IReadOnlySet<string> BookmarkedUrlKeys() =>
        Read<IReadOnlySet<string>>(db => db.Query("SELECT url_key FROM bookmarks WHERE url_key IS NOT NULL", row => row.String(0))
            .ToHashSet(StringComparer.Ordinal), new HashSet<string>());

    /// <summary>Bookmarks whose title, address or tags contain every word of <paramref name="text"/>.</summary>
    public IReadOnlyList<Bookmark> SearchBookmarks(string text, int limit = 500)
    {
        var terms = SuggestionRanker.Terms(text);
        if (terms.Count == 0) return [];
        var clauses = new List<string>();
        var arguments = new List<object?>();
        foreach (var term in terms)
        {
            clauses.Add("""
                (b.title LIKE ? ESCAPE '\' OR b.url LIKE ? ESCAPE '\'
                 OR EXISTS (SELECT 1 FROM bookmark_tags t WHERE t.bookmark_id = b.id AND t.tag LIKE ? ESCAPE '\'))
                """);
            var pattern = LikePattern(term);
            arguments.AddRange([pattern, pattern, pattern]);
        }
        arguments.Add(limit);
        var sql = $"SELECT {BookmarkColumns} FROM bookmarks b WHERE b.kind = 'url' AND "
            + string.Join(" AND ", clauses) + " ORDER BY b.title COLLATE NOCASE LIMIT ?";
        return Read(db => db.Query(sql, ReadBookmark, [.. arguments]), []);
    }

    /// <summary>Every tag in use, alphabetically.</summary>
    public IReadOnlyList<string> AllTags() =>
        Read(db => db.Query("SELECT DISTINCT tag FROM bookmark_tags ORDER BY tag COLLATE NOCASE", row => row.String(0)), []);

    /// <summary>Adds a bookmark at <paramref name="position"/> in <paramref name="parentId"/> (appended when null).</summary>
    public Bookmark AddBookmark(string url, string title, string parentId = Library.Bookmark.OtherId,
        IEnumerable<string>? tags = null, int? position = null, DateTimeOffset? created = null) =>
        Insert(BookmarkKind.Url, title, url, parentId, tags ?? [], position, created ?? Now);

    public Bookmark AddFolder(string title, string parentId = Library.Bookmark.OtherId, int? position = null,
        DateTimeOffset? created = null) =>
        Insert(BookmarkKind.Folder, title, null, parentId, [], position, created ?? Now);

    private Bookmark Insert(BookmarkKind kind, string title, string? url, string parentId, IEnumerable<string> tags,
        int? position, DateTimeOffset created) =>
        Locked(db => db.Transaction(() => InsertUnlocked(kind, title, url, parentId, tags, position, created, db)));

    /// <summary>Caller holds the lock and a transaction.</summary>
    private static Bookmark InsertUnlocked(BookmarkKind kind, string title, string? url, string parentId,
        IEnumerable<string> tags, int? position, DateTimeOffset created, SqliteDatabase db)
    {
        if (!IsFolder(parentId, db)) throw new LibraryException("No such folder");
        var count = (int)db.Scalar("SELECT count(*) FROM bookmarks WHERE parent_id = ?", parentId);
        var index = Math.Min(Math.Max(position ?? count, 0), count);
        if (index < count)
        {
            db.Run("UPDATE bookmarks SET position = position + 1 WHERE parent_id = ? AND position >= ?", parentId, index);
        }
        // Stored to the millisecond, so hand back what a later read would see.
        var stamp = SqliteDatabase.FromMillis(created.ToUnixTimeMilliseconds());
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            ParentId = parentId,
            Kind = kind,
            Title = title,
            Url = url,
            Position = index,
            Tags = CleanTags(tags),
            Created = stamp,
            Modified = stamp,
        };
        db.Run("""
            INSERT INTO bookmarks (id, parent_id, kind, title, url, url_key, position, created, modified)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, bookmark.Id, parentId, Library.Bookmark.KindId(kind), title, url,
            url is null ? null : LibraryUrl.Normalized(url), index, stamp, stamp);
        WriteTags(bookmark.Tags, bookmark.Id, db);
        return bookmark;
    }

    /// <summary>Changes a bookmark's title, address and tags (any null argument stays as it is).</summary>
    public void UpdateBookmark(string id, string? title = null, string? url = null, IEnumerable<string>? tags = null) =>
        Locked(db => db.Transaction(() =>
        {
            var now = Now;
            if (title is not null) db.Run("UPDATE bookmarks SET title = ?, modified = ? WHERE id = ?", title, now, id);
            if (url is not null)
            {
                db.Run("UPDATE bookmarks SET url = ?, url_key = ?, modified = ? WHERE id = ? AND kind = 'url'",
                    url, LibraryUrl.Normalized(url), now, id);
            }
            if (tags is not null)
            {
                db.Run("DELETE FROM bookmark_tags WHERE bookmark_id = ?", id);
                WriteTags(CleanTags(tags), id, db);
                db.Run("UPDATE bookmarks SET modified = ? WHERE id = ?", now, id);
            }
        }));

    /// <summary>
    /// Moves a bookmark or folder to <paramref name="position"/> in <paramref name="parentId"/>
    /// (the end when null). A folder cannot move into itself or its own subfolders; the roots cannot move.
    /// </summary>
    public void MoveBookmark(string id, string parentId, int? position = null)
    {
        if (Library.Bookmark.RootIds.Contains(id)) throw new LibraryException("The root folders stay put");
        Locked(db => db.Transaction(() =>
        {
            if (!IsFolder(parentId, db)) throw new LibraryException("No such folder");
            // Walk up from the destination: meeting `id` means a folder into its own subtree.
            string? cursor = parentId;
            while (cursor is not null)
            {
                if (cursor == id) throw new LibraryException("A folder cannot go inside itself");
                cursor = db.Query("SELECT parent_id FROM bookmarks WHERE id = ?", row => row.OptionalString(0), cursor).FirstOrDefault();
            }
            var oldParent = db.Query("SELECT parent_id FROM bookmarks WHERE id = ?", row => row.OptionalString(0), id).FirstOrDefault()
                ?? throw new LibraryException("No such bookmark");

            var siblings = db.Query("SELECT id FROM bookmarks WHERE parent_id = ? AND id != ? ORDER BY position",
                row => row.String(0), parentId, id);
            var index = Math.Min(Math.Max(position ?? siblings.Count, 0), siblings.Count);
            siblings.Insert(index, id);
            db.Run("UPDATE bookmarks SET parent_id = ?, modified = ? WHERE id = ?", parentId, Now, id);
            Renumber(siblings, db);
            if (oldParent != parentId) Compact(oldParent, db);
        }));
    }

    /// <summary>Deletes a bookmark, or a folder with everything in it. The roots cannot be deleted.</summary>
    public void DeleteBookmark(string id)
    {
        if (Library.Bookmark.RootIds.Contains(id)) return;
        Locked(db => db.Transaction(() =>
        {
            var parent = db.Query("SELECT parent_id FROM bookmarks WHERE id = ?", row => row.OptionalString(0), id).FirstOrDefault();
            db.Run("DELETE FROM bookmarks WHERE id = ?", id);
            if (parent is not null) Compact(parent, db);
        }));
    }

    /// <summary>Deletes every bookmark of <paramref name="url"/>. Returns how many there were.</summary>
    public int RemoveBookmarksForUrl(string url)
    {
        var ids = BookmarksForUrl(url).Select(bookmark => bookmark.Id).ToList();
        foreach (var id in ids) DeleteBookmark(id);
        return ids.Count;
    }

    public int BookmarkCount() => Read(db => (int)db.Scalar("SELECT count(*) FROM bookmarks WHERE kind = 'url'"), 0);

    private static bool IsFolder(string id, SqliteDatabase db) =>
        db.Scalar("SELECT count(*) FROM bookmarks WHERE id = ? AND kind = 'folder'", id) > 0;

    private static void Renumber(IReadOnlyList<string> ids, SqliteDatabase db)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            db.Run("UPDATE bookmarks SET position = ? WHERE id = ?", index, ids[index]);
        }
    }

    private static void Compact(string parentId, SqliteDatabase db) =>
        Renumber(db.Query("SELECT id FROM bookmarks WHERE parent_id = ? ORDER BY position", row => row.String(0), parentId), db);

    private static void WriteTags(IEnumerable<string> tags, string id, SqliteDatabase db)
    {
        foreach (var tag in tags) db.Run("INSERT OR IGNORE INTO bookmark_tags (bookmark_id, tag) VALUES (?, ?)", id, tag);
    }

    /// <summary>Trimmed, non-empty, without duplicates (ignoring case).</summary>
    public static IReadOnlyList<string> CleanTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0 && seen.Add(tag.ToLowerInvariant()))];
    }

    // ---- Importing a tree ----

    /// <summary>
    /// Adds bookmarks read from a file: the other browser's toolbar folder goes into the
    /// bookmarks bar, everything else into Other Bookmarks. Folders with the same name are
    /// merged and an address already in the same folder is skipped, so importing the same
    /// file twice adds nothing. Returns the number of bookmarks added.
    /// </summary>
    public int ImportBookmarks(IEnumerable<ImportedBookmark> items) => Locked(db => db.Transaction(() =>
    {
        var added = 0;
        foreach (var item in items)
        {
            added += item.IsToolbar
                ? ImportUnlocked(item.Children, Library.Bookmark.BarId, db)
                : ImportUnlocked([item], Library.Bookmark.OtherId, db);
        }
        return added;
    }));

    /// <summary>Adds an imported tree under <paramref name="parentId"/> (merging as <see cref="ImportBookmarks"/> does).</summary>
    public int ImportTree(IEnumerable<ImportedBookmark> items, string parentId) =>
        Locked(db => db.Transaction(() => ImportUnlocked(items, parentId, db)));

    private int ImportUnlocked(IEnumerable<ImportedBookmark> items, string parentId, SqliteDatabase db)
    {
        var added = 0;
        foreach (var item in items)
        {
            if (item.IsFolder)
            {
                var folderId = db.Query("SELECT id FROM bookmarks WHERE parent_id = ? AND kind = 'folder' AND title = ? LIMIT 1",
                        row => row.String(0), parentId, item.Title).FirstOrDefault()
                    ?? InsertUnlocked(BookmarkKind.Folder, item.Title, null, parentId, [], null, item.Added ?? Now, db).Id;
                added += ImportUnlocked(item.Children, folderId, db);
            }
            else if (item.Url is { Length: > 0 } url)
            {
                var duplicate = db.Scalar("SELECT count(*) FROM bookmarks WHERE parent_id = ? AND url_key = ?",
                    parentId, LibraryUrl.Normalized(url)) > 0;
                if (duplicate) continue;
                InsertUnlocked(BookmarkKind.Url, item.Title, url, parentId, item.Tags, null, item.Added ?? Now, db);
                added++;
            }
        }
        return added;
    }

    // ---- History ----

    private const string EntryColumns = "id, url, title, host, visit_count, typed_count, last_visit, frecency";

    private static HistoryEntry ReadEntry(SqliteDatabase.Row row) => new(
        row.Int(0), row.String(1), row.String(2), row.String(3), (int)row.Int(4), (int)row.Int(5), row.Time(6), row.Double(7));

    /// <summary>Records one visit. <paramref name="title"/> replaces the stored title when it is not empty.</summary>
    public void RecordVisit(string url, string title, bool typed = false, DateTimeOffset? at = null)
    {
        if (!LibraryUrl.IsRecordable(url)) return;
        var when = at ?? Now;
        Locked(db => db.Transaction(() =>
        {
            db.Run("""
                INSERT INTO history_urls (url, title, host, visit_count, typed_count, last_visit)
                VALUES (?1, ?2, ?3, 1, ?4, ?5)
                ON CONFLICT(url) DO UPDATE SET
                  visit_count = visit_count + 1,
                  typed_count = typed_count + ?4,
                  title = CASE WHEN ?2 = '' THEN title ELSE ?2 END,
                  last_visit = max(last_visit, ?5)
                """, url, title, LibraryUrl.Host(url), typed ? 1 : 0, when);
            var urlId = db.Scalar("SELECT id FROM history_urls WHERE url = ?", url);
            db.Run("INSERT INTO history_visits (url_id, time, typed) VALUES (?, ?, ?)", urlId, when, typed);
            UpdateFrecency(urlId, Now, db);
        }));
    }

    /// <summary>A page's title arrived (or changed) after its visit was recorded.</summary>
    public void UpdateTitle(string url, string title)
    {
        if (title.Length == 0) return;
        Read(db => db.Run("UPDATE history_urls SET title = ? WHERE url = ?", title, url), 0);
    }

    private static void UpdateFrecency(long urlId, DateTimeOffset now, SqliteDatabase db)
    {
        var visits = db.Query("SELECT time, typed FROM history_visits WHERE url_id = ? ORDER BY time DESC LIMIT ?",
            row => (Time: row.Time(0), Typed: row.Bool(1)), urlId, Frecency.SampleSize);
        var count = (int)db.Scalar("SELECT visit_count FROM history_urls WHERE id = ?", urlId);
        db.Run("UPDATE history_urls SET frecency = ? WHERE id = ?", Frecency.Score(count, visits, now), urlId);
    }

    /// <summary>
    /// Scores decay with time even without new visits; recompute them all (in the background,
    /// about once a day). Works in slices so a large history never holds the lock long.
    /// </summary>
    public void RecomputeFrecency(DateTimeOffset? now = null)
    {
        var at = now ?? Now;
        var ids = Read(db => db.Query("SELECT id FROM history_urls", row => row.Int(0)), []);
        for (var slice = 0; slice < ids.Count; slice += 200)
        {
            var start = slice;
            Read(db =>
            {
                db.Transaction(() =>
                {
                    foreach (var id in ids.Skip(start).Take(200)) UpdateFrecency(id, at, db);
                });
                return 0;
            }, 0);
        }
        SetMeta("frecencyComputed", at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>When <see cref="RecomputeFrecency"/> last ran.</summary>
    public DateTimeOffset? FrecencyComputed =>
        long.TryParse(Meta("frecencyComputed"), System.Globalization.CultureInfo.InvariantCulture, out var millis)
            ? SqliteDatabase.FromMillis(millis) : null;

    /// <summary>Addresses by frecency, best first (the address bar's in-memory index).</summary>
    public IReadOnlyList<HistoryEntry> TopHistory(int limit) =>
        Read(db => db.Query($"SELECT {EntryColumns} FROM history_urls WHERE frecency > 0 ORDER BY frecency DESC LIMIT ?", ReadEntry, limit), []);

    /// <summary>Addresses whose address or title contains every word of <paramref name="text"/>, best first.</summary>
    public IReadOnlyList<HistoryEntry> SearchHistory(string text, int limit)
    {
        var terms = SuggestionRanker.Terms(text);
        if (terms.Count == 0) return [];
        var clauses = terms.Select(_ => "(url LIKE ? ESCAPE '\\' OR title LIKE ? ESCAPE '\\')");
        var arguments = terms.SelectMany(term => new object?[] { LikePattern(term), LikePattern(term) }).Append(limit).ToArray();
        var sql = $"SELECT {EntryColumns} FROM history_urls WHERE " + string.Join(" AND ", clauses) + " ORDER BY frecency DESC LIMIT ?";
        return Read(db => db.Query(sql, ReadEntry, arguments), []);
    }

    /// <summary>The most visited sites, one address per site.</summary>
    public IReadOnlyList<HistoryEntry> TopSites(int limit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. TopHistory(Math.Max(limit * 12, 100))
            .Where(entry => seen.Add(entry.Host.Length == 0 ? entry.Url : entry.Host))
            .Take(limit)];
    }

    /// <summary>Most recently visited addresses (the History menu).</summary>
    public IReadOnlyList<HistoryEntry> RecentHistory(int limit) =>
        Read(db => db.Query($"SELECT {EntryColumns} FROM history_urls ORDER BY last_visit DESC LIMIT ?", ReadEntry, limit), []);

    public HistoryEntry? HistoryEntry(string url) =>
        Read(db => db.Query($"SELECT {EntryColumns} FROM history_urls WHERE url = ?", ReadEntry, url).FirstOrDefault(), null);

    /// <summary>
    /// Visits newest first, optionally filtered (every word in the address or title), only
    /// those before <paramref name="before"/> (for paging) and at or after <paramref name="since"/>.
    /// </summary>
    public IReadOnlyList<HistoryVisit> Visits(string matching = "", DateTimeOffset? before = null, int limit = 500,
        DateTimeOffset? since = null)
    {
        var clauses = new List<string>();
        var arguments = new List<object?>();
        foreach (var term in SuggestionRanker.Terms(matching))
        {
            clauses.Add("(u.url LIKE ? ESCAPE '\\' OR u.title LIKE ? ESCAPE '\\')");
            arguments.AddRange([LikePattern(term), LikePattern(term)]);
        }
        if (before is { } cutoff)
        {
            clauses.Add("v.time < ?");
            arguments.Add(cutoff);
        }
        if (since is { } start)
        {
            clauses.Add("v.time >= ?");
            arguments.Add(start);
        }
        arguments.Add(limit);
        var sql = $"""
            SELECT v.id, u.id, u.url, u.title, u.host, v.time, v.typed
            FROM history_visits v JOIN history_urls u ON u.id = v.url_id
            {(clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses))}
            ORDER BY v.time DESC, v.id DESC LIMIT ?
            """;
        return Read(db => db.Query(sql, row => new HistoryVisit(
            row.Int(0), row.Int(1), row.String(2), row.String(3), row.String(4), row.Time(5), row.Bool(6)), [.. arguments]), []);
    }

    public HistoryCounts HistoryCounts() => Read(db => new HistoryCounts(
        (int)db.Scalar("SELECT count(*) FROM history_urls"),
        (int)db.Scalar("SELECT count(*) FROM history_visits")), default);

    /// <summary>Deletes single visits (the address goes too once it has none left).</summary>
    public void DeleteVisits(IEnumerable<long> ids) => Locked(db => db.Transaction(() =>
    {
        var urlIds = new HashSet<long>();
        foreach (var id in ids)
        {
            var urlId = db.Query("SELECT url_id FROM history_visits WHERE id = ?", row => (long?)row.Int(0), id).FirstOrDefault();
            if (urlId is not { } found) continue;
            urlIds.Add(found);
            db.Run("DELETE FROM history_visits WHERE id = ?", id);
        }
        foreach (var urlId in urlIds) RefreshAggregates(urlId, db);
    }));

    /// <summary>Deletes an address and all its visits.</summary>
    public void DeleteHistory(string url) => Locked(db => { db.Run("DELETE FROM history_urls WHERE url = ?", url); });

    /// <summary>
    /// Deletes everything from a site (<c>example.com</c> also covers its subdomains).
    /// Returns how many addresses went.
    /// </summary>
    public int DeleteHistorySite(string site)
    {
        var host = LibraryUrl.Host("https://" + site.Trim().ToLowerInvariant());
        if (host.Length == 0) return 0;
        return Locked(db => db.Run("DELETE FROM history_urls WHERE host = ? OR host LIKE ? ESCAPE '\\'", host, "%." + EscapeLike(host)));
    }

    /// <summary>Deletes visits made at or after <paramref name="since"/> (everything when null).</summary>
    public void ClearHistory(DateTimeOffset? since) => Locked(db => db.Transaction(() =>
    {
        if (since is not { } cutoff)
        {
            db.Execute("DELETE FROM history_visits; DELETE FROM history_urls;");
            return;
        }
        var affected = db.Query("SELECT DISTINCT url_id FROM history_visits WHERE time >= ?", row => row.Int(0), cutoff);
        db.Run("DELETE FROM history_visits WHERE time >= ?", cutoff);
        foreach (var urlId in affected) RefreshAggregates(urlId, db);
    }));

    /// <summary>Re-derives an address's counts from its remaining visits; deletes it when none remain.</summary>
    private void RefreshAggregates(long urlId, SqliteDatabase db)
    {
        var count = db.Scalar("SELECT count(*) FROM history_visits WHERE url_id = ?", urlId);
        if (count == 0)
        {
            db.Run("DELETE FROM history_urls WHERE id = ?", urlId);
            return;
        }
        db.Run("""
            UPDATE history_urls SET
              visit_count = ?2,
              typed_count = (SELECT count(*) FROM history_visits WHERE url_id = ?1 AND typed = 1),
              last_visit = (SELECT max(time) FROM history_visits WHERE url_id = ?1)
            WHERE id = ?1
            """, urlId, count);
        UpdateFrecency(urlId, Now, db);
    }

    // ---- Reading list ----

    private static ReadingListItem ReadItem(SqliteDatabase.Row row) =>
        new(row.String(0), row.String(1), row.String(2), row.Time(3), row.IsNull(4) ? null : row.Time(4));

    /// <summary>Unread first, then newest first.</summary>
    public IReadOnlyList<ReadingListItem> ReadingList() => Read(db => db.Query("""
        SELECT id, url, title, added, read_at FROM reading_list
        ORDER BY read_at IS NOT NULL, added DESC
        """, ReadItem), []);

    /// <summary>Adds a page (or, if it is already there, marks it unread again and moves it to the top).</summary>
    public ReadingListItem AddToReadingList(string url, string title, DateTimeOffset? at = null) => Locked(db =>
    {
        db.Run("""
            INSERT INTO reading_list (id, url, title, added) VALUES (?1, ?2, ?3, ?4)
            ON CONFLICT(url) DO UPDATE SET read_at = NULL, added = ?4,
              title = CASE WHEN ?3 = '' THEN title ELSE ?3 END
            """, Guid.NewGuid().ToString().ToUpperInvariant(), url, title, at ?? Now);
        return db.Query("SELECT id, url, title, added, read_at FROM reading_list WHERE url = ?", ReadItem, url)[0];
    });

    public void SetRead(string id, bool read) =>
        Locked(db => { db.Run("UPDATE reading_list SET read_at = ? WHERE id = ?", read ? Now : null, id); });

    public void RemoveFromReadingList(string id) => Locked(db => { db.Run("DELETE FROM reading_list WHERE id = ?", id); });

    public bool IsInReadingList(string url) =>
        Read(db => db.Scalar("SELECT count(*) FROM reading_list WHERE url = ?", url) > 0, false);

    // ---- Custom search engines ----

    public IReadOnlyList<CustomSearchEngine> CustomEngines() => Read(db => db.Query(
        "SELECT id, name, keyword, template, suggest_template FROM search_engines ORDER BY position, name",
        row => new CustomSearchEngine
        {
            Id = row.String(0), Name = row.String(1), Keyword = row.String(2), Template = row.String(3),
            SuggestTemplate = row.OptionalString(4),
        }), []);

    /// <summary>Adds or replaces an engine (matched by id).</summary>
    public void SaveCustomEngine(CustomSearchEngine engine) => Locked(db =>
    {
        var position = db.Scalar("SELECT coalesce(max(position) + 1, 0) FROM search_engines");
        db.Run("""
            INSERT INTO search_engines (id, name, keyword, template, suggest_template, position)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, keyword = excluded.keyword,
              template = excluded.template, suggest_template = excluded.suggest_template
            """, engine.Id, engine.Name, engine.Keyword, engine.Template, engine.SuggestTemplate, position);
    });

    public void DeleteCustomEngine(string id) => Locked(db => { db.Run("DELETE FROM search_engines WHERE id = ?", id); });

    // ---- Meta (also the Windows library's own preferences, under "pref.") ----

    public string? Meta(string key) =>
        Read(db => db.Query("SELECT value FROM meta WHERE key = ?", row => row.String(0), key).FirstOrDefault(), null);

    public void SetMeta(string key, string? value) => Read(db => value is null
        ? db.Run("DELETE FROM meta WHERE key = ?", key)
        : db.Run("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value), 0);

    // ---- Helpers ----

    internal static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    internal static string LikePattern(string term) => "%" + EscapeLike(term) + "%";
}
