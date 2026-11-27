import Foundation
import SQLite3

/// A small wrapper around one SQLite connection (the system `SQLite3` module, no packages).
///
/// Not thread-safe by itself: `LibraryStore` serialises every use behind its lock.
final class SQLiteDatabase {
    enum Value: Equatable {
        case int(Int64)
        case real(Double)
        case text(String)
        case null

        init(_ value: Int) { self = .int(Int64(value)) }
        init(_ value: Int64) { self = .int(value) }
        init(_ value: Bool) { self = .int(value ? 1 : 0) }
        init(_ value: String?) { self = value.map(Value.text) ?? .null }
        init(_ value: Double) { self = .real(value) }
    }

    struct Row {
        fileprivate let statement: OpaquePointer

        func int(_ column: Int32) -> Int64 { sqlite3_column_int64(statement, column) }
        func double(_ column: Int32) -> Double { sqlite3_column_double(statement, column) }
        func bool(_ column: Int32) -> Bool { sqlite3_column_int64(statement, column) != 0 }
        func isNull(_ column: Int32) -> Bool { sqlite3_column_type(statement, column) == SQLITE_NULL }
        func string(_ column: Int32) -> String { optionalString(column) ?? "" }
        func optionalString(_ column: Int32) -> String? {
            sqlite3_column_text(statement, column).map { String(cString: $0) }
        }
    }

    struct Failure: Error, CustomStringConvertible {
        let description: String
    }

    private var handle: OpaquePointer?
    private var statements: [String: OpaquePointer] = [:]

    /// Opens (creating if needed) the database at `url`, or an in-memory one when nil.
    init(url: URL?) throws {
        let path: String
        if let url {
            try FileManager.default.createDirectory(
                at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            path = url.path
        } else {
            path = ":memory:"
        }
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_NOMUTEX
        guard sqlite3_open_v2(path, &handle, flags, nil) == SQLITE_OK else {
            let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "out of memory"
            sqlite3_close_v2(handle)
            handle = nil
            throw Failure(description: "Could not open \(path): \(message)")
        }
        sqlite3_busy_timeout(handle, 2000)
        try execute("PRAGMA foreign_keys = ON")
        if url != nil {
            // WAL keeps readers and the writer out of each other's way and survives crashes.
            try execute("PRAGMA journal_mode = WAL")
            try execute("PRAGMA synchronous = NORMAL")
        }
    }

    deinit {
        for statement in statements.values { sqlite3_finalize(statement) }
        sqlite3_close_v2(handle)
    }

    var userVersion: Int {
        get { Int((try? query("PRAGMA user_version") { $0.int(0) }.first) ?? 0) }
        set { try? execute("PRAGMA user_version = \(newValue)") }
    }

    var changes: Int { Int(sqlite3_changes(handle)) }
    var lastInsertID: Int64 { sqlite3_last_insert_rowid(handle) }

    /// Runs one or more statements without parameters.
    func execute(_ sql: String) throws {
        var message: UnsafeMutablePointer<CChar>?
        guard sqlite3_exec(handle, sql, nil, nil, &message) == SQLITE_OK else {
            let text = message.map { String(cString: $0) } ?? "unknown error"
            sqlite3_free(message)
            throw Failure(description: text)
        }
    }

    /// Runs one statement with parameters. Returns the number of rows changed.
    @discardableResult
    func run(_ sql: String, _ arguments: Value...) throws -> Int {
        try run(sql, arguments: arguments)
    }

    @discardableResult
    func run(_ sql: String, arguments: [Value]) throws -> Int {
        try withStatement(sql, arguments) { statement in
            let code = sqlite3_step(statement)
            guard code == SQLITE_DONE || code == SQLITE_ROW else { throw failure() }
        }
        return changes
    }

    func query<T>(_ sql: String, _ arguments: Value..., map: (Row) throws -> T) throws -> [T] {
        try query(sql, arguments: arguments, map: map)
    }

    func query<T>(_ sql: String, arguments: [Value], map: (Row) throws -> T) throws -> [T] {
        try withStatement(sql, arguments) { statement in
            var rows: [T] = []
            while true {
                let code = sqlite3_step(statement)
                if code == SQLITE_DONE { break }
                guard code == SQLITE_ROW else { throw failure() }
                rows.append(try map(Row(statement: statement)))
            }
            return rows
        }
    }

    func scalar(_ sql: String, _ arguments: Value...) throws -> Int64 {
        try query(sql, arguments: arguments) { $0.int(0) }.first ?? 0
    }

    /// Runs `body` in a transaction, rolling back if it throws.
    func transaction<T>(_ body: () throws -> T) throws -> T {
        try execute("BEGIN IMMEDIATE")
        do {
            let result = try body()
            try execute("COMMIT")
            return result
        } catch {
            try? execute("ROLLBACK")
            throw error
        }
    }

    /// Prepared statements are cached per SQL text: the hot paths (recording a visit,
    /// suggestion lookups) run the same few statements over and over.
    private func withStatement<T>(_ sql: String, _ arguments: [Value], _ body: (OpaquePointer) throws -> T) throws -> T {
        let statement: OpaquePointer
        if let cached = statements[sql] {
            statement = cached
        } else {
            var prepared: OpaquePointer?
            guard sqlite3_prepare_v2(handle, sql, -1, &prepared, nil) == SQLITE_OK, let prepared else {
                throw failure()
            }
            statements[sql] = prepared
            statement = prepared
        }
        defer {
            sqlite3_reset(statement)
            sqlite3_clear_bindings(statement)
        }
        for (offset, argument) in arguments.enumerated() {
            let index = Int32(offset + 1)
            switch argument {
            case .int(let value): sqlite3_bind_int64(statement, index, value)
            case .real(let value): sqlite3_bind_double(statement, index, value)
            case .text(let value): sqlite3_bind_text(statement, index, value, -1, Self.transient)
            case .null: sqlite3_bind_null(statement, index)
            }
        }
        return try body(statement)
    }

    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    private func failure() -> Failure {
        Failure(description: handle.map { String(cString: sqlite3_errmsg($0)) } ?? "no database")
    }
}

extension Date {
    var libraryMillis: Int64 { Int64((timeIntervalSince1970 * 1000).rounded()) }
    init(libraryMillis: Int64) { self.init(timeIntervalSince1970: Double(libraryMillis) / 1000) }
}
