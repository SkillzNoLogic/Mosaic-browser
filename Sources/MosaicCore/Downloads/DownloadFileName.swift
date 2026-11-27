import Foundation

/// File names for downloads: turning whatever a server or page suggested into a safe name
/// for the local file system, and finding a free name in the download folder.
public enum DownloadFileName {
    /// Used when nothing usable is left of the suggested name.
    public static let fallback = "download"

    /// Extensions made of two parts that must stay together when a number is inserted
    /// ("archive (1).tar.gz", not "archive.tar (1).gz").
    static let compoundExtensions: Set<String> = ["tar.gz", "tar.bz2", "tar.xz", "tar.zst", "tar.lz", "tar.z"]

    /// Characters that reorder text on screen (right-to-left override and friends). A name
    /// such as "invoice<U+202E>fdp.app" displays as "invoiceppa.pdf"; they are never
    /// legitimate in a file name, so they are removed (and flagged by `DownloadSafety`).
    public static let bidiControls: Set<Unicode.Scalar> = [
        "\u{200E}", "\u{200F}", "\u{061C}",
        "\u{202A}", "\u{202B}", "\u{202C}", "\u{202D}", "\u{202E}",
        "\u{2066}", "\u{2067}", "\u{2068}", "\u{2069}",
    ]

    /// Makes `suggested` safe to use as a single file name on macOS:
    /// - only the last path component is kept (no "../", no folders);
    /// - control characters, bidirectional controls, ":" and "/" are removed or replaced;
    /// - leading dots (hidden files) and surrounding spaces/dots are trimmed;
    /// - the name is limited to 255 UTF-8 bytes, keeping its extension.
    public static func sanitize(_ suggested: String) -> String {
        var name = suggested
        // Keep only the last component, whichever separator the source used.
        if let last = name.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last {
            name = String(last)
        } else {
            name = ""
        }
        var scalars = String.UnicodeScalarView()
        for scalar in name.unicodeScalars {
            if bidiControls.contains(scalar) { continue }
            // The zero-width joiner holds emoji sequences together; keep it.
            if scalar == "\u{200D}" { scalars.append(scalar); continue }
            switch scalar.properties.generalCategory {
            case .control, .format, .lineSeparator, .paragraphSeparator, .unassigned, .privateUse, .surrogate:
                continue
            default:
                break
            }
            // ":" is shown as "/" by the Finder and is reserved by HFS paths.
            scalars.append(scalar == ":" ? "-" : scalar)
        }
        name = String(scalars)
        // Collapse runs of whitespace (used to push a real extension out of sight).
        name = name.replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        name = name.trimmingCharacters(in: CharacterSet(charactersIn: " .\t"))
        // Hidden files are not what anyone means to download.
        while name.hasPrefix(".") { name.removeFirst() }
        name = name.trimmingCharacters(in: .whitespaces)
        if name.isEmpty || name == "." || name == ".." { return fallback }
        return limitLength(name, maxBytes: 255)
    }

    /// The name split into stem and extension (lowercased extension not applied). Compound
    /// archive extensions are kept whole. A name with no extension has an empty one.
    public static func split(_ name: String) -> (stem: String, ext: String) {
        let lower = name.lowercased()
        for compound in compoundExtensions where lower.hasSuffix("." + compound) && lower.count > compound.count + 1 {
            let stem = String(name.dropLast(compound.count + 1))
            return (stem, String(name.suffix(compound.count)))
        }
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else { return (name, "") }
        let ext = String(name[name.index(after: dot)...])
        // "version 1.2" or "notes." are not extensions.
        guard !ext.isEmpty, ext.count <= 12, !ext.contains(" ") else { return (name, "") }
        return (String(name[..<dot]), ext)
    }

    /// The lowercased extension of `name` (the last one, "gz" for "a.tar.gz").
    public static func pathExtension(_ name: String) -> String {
        let ext = split(name).ext.lowercased()
        if let dot = ext.lastIndex(of: ".") { return String(ext[ext.index(after: dot)...]) }
        return ext
    }

    /// `name`, or "name (1).ext", "name (2).ext"… — the first one `exists` says is free.
    public static func unique(_ name: String, exists: (String) -> Bool) -> String {
        guard exists(name) else { return name }
        let (stem, ext) = split(name)
        let suffix = ext.isEmpty ? "" : "." + ext
        for number in 1..<10_000 {
            let candidate = limitLength("\(stem) (\(number))\(suffix)", maxBytes: 255)
            if !exists(candidate) { return candidate }
        }
        return limitLength("\(stem) \(UUID().uuidString.prefix(8))\(suffix)", maxBytes: 255)
    }

    /// A name for a URL that came without one (no Content-Disposition): its last path
    /// component, percent-decoded, or the host.
    public static func suggested(forURL url: URL) -> String {
        let last = url.lastPathComponent
        if !last.isEmpty, last != "/" {
            return sanitize(last.removingPercentEncoding ?? last)
        }
        return sanitize(url.host ?? fallback)
    }

    static func limitLength(_ name: String, maxBytes: Int) -> String {
        guard name.utf8.count > maxBytes else { return name }
        let (stem, ext) = split(name)
        let suffix = ext.isEmpty ? "" : "." + ext
        var trimmed = stem
        while !trimmed.isEmpty, trimmed.utf8.count + suffix.utf8.count > maxBytes {
            trimmed.removeLast()
        }
        return trimmed.isEmpty ? String(name.prefix(64)) : trimmed + suffix
    }
}
