import Foundation

/// Password CSV files, as exported by Chrome (and Edge, Brave, Opera), Firefox, Safari and
/// most password managers, and as Mosaic exports them.
///
/// Columns are found by their header, so the column order and extra columns do not matter:
/// - Chrome:  `name,url,username,password,note`
/// - Firefox: `url,username,password,httpRealm,formActionOrigin,guid,timeCreated,…`
/// - Safari:  `Title,URL,Username,Password,Notes,OTPAuth`
public enum PasswordCSV {
    public struct Entry: Equatable, Sendable {
        public let origin: SiteOrigin
        public let username: String
        public let password: String

        public init(origin: SiteOrigin, username: String, password: String) {
            self.origin = origin
            self.username = username
            self.password = password
        }

        public var id: CredentialID { CredentialID(origin: origin, username: username) }
    }

    public struct Skipped: Equatable, Sendable {
        /// 1-based line of the record in the file (the header is line 1).
        public let line: Int
        public let reason: String
    }

    public struct ImportResult: Equatable, Sendable {
        public var entries: [Entry] = []
        public var skipped: [Skipped] = []
    }

    public enum ImportError: LocalizedError, Equatable {
        case empty
        case missingColumns

        public var errorDescription: String? {
            switch self {
            case .empty: "The file is empty."
            case .missingColumns:
                "This does not look like a password export: it needs columns named url, username and password."
            }
        }
    }

    static let urlColumns = ["url", "website", "origin", "login_uri", "login url", "web site", "hostname"]
    static let usernameColumns = ["username", "login", "user name", "login_username", "user", "email", "account"]
    static let passwordColumns = ["password", "login_password", "pass"]
    static let realmColumns = ["httprealm"]

    /// Reads an export. Rows that cannot be used (no password, not a web address, an HTTP
    /// authentication login, a duplicate) are skipped and reported, never guessed at.
    public static func parseExport(_ text: String) throws -> ImportResult {
        let rows = parse(text)
        guard let header = rows.first else { throw ImportError.empty }
        let names = header.map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
        func column(_ candidates: [String]) -> Int? {
            for candidate in candidates { if let index = names.firstIndex(of: candidate) { return index } }
            return nil
        }
        guard let urlColumn = column(urlColumns), let passwordColumn = column(passwordColumns) else {
            throw ImportError.missingColumns
        }
        let usernameColumn = column(usernameColumns)
        let realmColumn = column(realmColumns)

        var result = ImportResult()
        var seen: Set<CredentialID> = []
        for (offset, row) in rows.dropFirst().enumerated() {
            let line = offset + 2
            func field(_ index: Int?) -> String {
                guard let index, index < row.count else { return "" }
                return row[index]
            }
            let password = field(passwordColumn)
            let address = field(urlColumn).trimmingCharacters(in: .whitespaces)
            guard !password.isEmpty else {
                result.skipped.append(Skipped(line: line, reason: "no password"))
                continue
            }
            if !field(realmColumn).isEmpty {
                result.skipped.append(Skipped(line: line, reason: "HTTP authentication login (not supported)"))
                continue
            }
            guard let origin = SiteOrigin(url: address) ?? (address.contains("://") ? nil : SiteOrigin(userInput: address)),
                  origin.host.contains(".") || origin.host == "localhost" || origin.host.contains(":")
            else {
                result.skipped.append(Skipped(line: line, reason: "not a web address (\(address.prefix(40)))"))
                continue
            }
            let entry = Entry(origin: origin, username: field(usernameColumn), password: password)
            guard seen.insert(entry.id).inserted else {
                result.skipped.append(Skipped(line: line, reason: "duplicate of an earlier row"))
                continue
            }
            result.entries.append(entry)
        }
        return result
    }

    /// Writes an export in Chrome's layout (`name,url,username,password`), which Chrome,
    /// Firefox, Safari and Mosaic all import.
    public static func export(_ entries: [Entry]) -> String {
        var lines = ["name,url,username,password"]
        for entry in entries.sorted(by: { $0.id < $1.id }) {
            lines.append([entry.origin.host, entry.origin.string, entry.username, entry.password]
                .map(quote).joined(separator: ","))
        }
        return lines.joined(separator: "\n") + "\n"
    }

    static func quote(_ field: String) -> String {
        let needsQuotes = field.contains(where: { $0 == "," || $0 == "\"" || $0 == "\n" || $0 == "\r" })
            || field.hasPrefix(" ") || field.hasSuffix(" ")
        guard needsQuotes else { return field }
        return "\"" + field.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    /// RFC 4180 records: quoted fields, doubled quotes inside them, CRLF or LF line ends,
    /// a leading byte-order mark. Blank lines are dropped.
    public static func parse(_ text: String) -> [[String]] {
        var rows: [[String]] = []
        var row: [String] = []
        var field = ""
        var quoted = false
        var scalars = Substring(text).unicodeScalars[...]
        if scalars.first == "\u{FEFF}" { scalars.removeFirst() }
        func endRow() {
            row.append(field)
            if !(row.count == 1 && row[0].isEmpty) { rows.append(row) }
            row = []
            field = ""
        }
        while let scalar = scalars.popFirst() {
            if quoted {
                if scalar == "\"" {
                    if scalars.first == "\"" {
                        field.unicodeScalars.append("\"")
                        scalars.removeFirst()
                    } else {
                        quoted = false
                    }
                } else {
                    field.unicodeScalars.append(scalar)
                }
                continue
            }
            switch scalar {
            case "\"" where field.isEmpty: quoted = true
            case ",":
                row.append(field)
                field = ""
            case "\r":
                if scalars.first == "\n" { scalars.removeFirst() }
                endRow()
            case "\n": endRow()
            default: field.unicodeScalars.append(scalar)
            }
        }
        if !field.isEmpty || !row.isEmpty { endRow() }
        return rows
    }
}
