import Foundation

/// Decides which downloads deserve a warning before they are kept or opened.
///
/// Mosaic never sends files or their details anywhere to decide this: every check here is
/// local and based on the file's name. macOS's own Gatekeeper still applies to whatever is
/// opened, because every download carries the quarantine attribute.
public enum DownloadSafety {
    /// Kinds of files that can run code or change the system when opened.
    public enum Risk: String, Codable, Sendable, CaseIterable {
        case application, installer, diskImage, script, configuration, windowsProgram

        /// Shown in warnings: "This file is <description>."
        public var description: String {
            switch self {
            case .application: "an application"
            case .installer: "an installer package"
            case .diskImage: "a disk image, which can contain applications"
            case .script: "a script that can run commands on your Mac"
            case .configuration: "a file that can change system or app settings"
            case .windowsProgram: "a Windows program"
            }
        }
    }

    /// Why a file's name could make it look like something it is not.
    public enum Misleading: Hashable, Codable, Sendable {
        /// Right-to-left override (or a similar control character) reorders the name on screen.
        case bidiOverride
        /// "invoice.pdf.app": looks like a document, but the last extension decides what it is.
        case doubleExtension(shown: String, real: String)
        /// Runs of spaces push the real extension out of view ("photo.jpg        .command").
        case paddedExtension

        public var description: String {
            switch self {
            case .bidiOverride:
                "Its name contains invisible characters that reverse part of the text, so the name you see is not its real name."
            case .doubleExtension(let shown, let real):
                "Its name ends in “.\(shown).\(real)”: it looks like a .\(shown) file but is really a .\(real) file."
            case .paddedExtension:
                "Its name hides its real extension behind a run of spaces."
            }
        }
    }

    /// Everything Mosaic knows about a file name's risk.
    public struct Assessment: Hashable, Codable, Sendable {
        public var risk: Risk?
        public var misleading: [Misleading]

        public init(risk: Risk? = nil, misleading: [Misleading] = []) {
            self.risk = risk
            self.misleading = misleading
        }

        /// True when the user should confirm before keeping or opening the file.
        public var needsWarning: Bool { !misleading.isEmpty || (risk != nil && risk != .windowsProgram) }

        /// One or two sentences for a warning.
        public var summary: String? {
            var parts: [String] = []
            if let risk, risk != .windowsProgram || !misleading.isEmpty { parts.append("This file is \(risk.description).") }
            parts += misleading.map(\.description)
            return parts.isEmpty ? nil : parts.joined(separator: " ")
        }
    }

    static let riskByExtension: [String: Risk] = {
        var map: [String: Risk] = [:]
        for ext in ["app", "prefpane", "saver", "qlgenerator", "kext", "plugin", "appex", "osax",
                    "workflow", "action", "widget", "systemextension", "xpc"] { map[ext] = .application }
        for ext in ["pkg", "mpkg"] { map[ext] = .installer }
        for ext in ["dmg", "iso", "img", "sparseimage", "sparsebundle", "toast", "cdr", "smi", "dart", "udif"] {
            map[ext] = .diskImage
        }
        for ext in ["command", "tool", "sh", "bash", "zsh", "csh", "ksh", "tcsh", "fish", "py", "pyc", "pl", "rb",
                    "scpt", "scptd", "applescript", "jar", "terminal", "inetloc", "fileloc", "shortcut",
                    "jnlp"] { map[ext] = .script }
        for ext in ["mobileconfig", "configprofile", "mobileprovision", "provisionprofile"] { map[ext] = .configuration }
        for ext in ["exe", "msi", "bat", "cmd", "com", "scr", "pif", "ps1", "vbs", "vbe", "js", "jse", "wsf",
                    "hta", "cpl", "lnk", "reg", "msix", "appx", "url"] { map[ext] = .windowsProgram }
        return map
    }()

    /// Extensions people trust at a glance; used to spot "invoice.pdf.app".
    static let documentLikeExtensions: Set<String> = [
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "csv", "odt", "ods", "odp",
        "pages", "numbers", "key", "jpg", "jpeg", "png", "gif", "heic", "webp", "bmp", "tif", "tiff",
        "mp3", "m4a", "wav", "mp4", "mov", "m4v", "avi", "mkv", "html", "htm", "zip", "epub",
    ]

    /// Files Mosaic may open by itself after downloading when the user asked for that:
    /// documents, pictures, audio and video. Never archives (they can unpack applications),
    /// never anything executable, never macro-enabled Office files.
    static let safeToOpenExtensions: Set<String> = [
        "pdf", "txt", "rtf", "md", "csv",
        "jpg", "jpeg", "png", "gif", "heic", "heif", "webp", "tif", "tiff", "bmp",
        "mp3", "m4a", "aac", "wav", "aif", "aiff", "flac", "ogg", "opus",
        "mp4", "m4v", "mov", "webm",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pages", "numbers", "key", "odt", "ods", "odp", "epub",
    ]

    /// The risk of a file, judged by its (last) extension.
    public static func risk(forFileName name: String) -> Risk? {
        riskByExtension[DownloadFileName.pathExtension(name)]
    }

    /// Ways `name` (as the server or page suggested it, before sanitising) could mislead.
    public static func misleadingReasons(_ name: String) -> [Misleading] {
        var reasons: [Misleading] = []
        if name.unicodeScalars.contains(where: { DownloadFileName.bidiControls.contains($0) }) {
            reasons.append(.bidiOverride)
        }
        let cleaned = String(String.UnicodeScalarView(
            name.unicodeScalars.filter { !DownloadFileName.bidiControls.contains($0) }))
        if cleaned.range(of: "\\s{3,}\\.[A-Za-z0-9]{1,12}$", options: .regularExpression) != nil
            || cleaned.range(of: "\\.[A-Za-z0-9]{1,12}\\s{3,}\\S", options: .regularExpression) != nil
        {
            reasons.append(.paddedExtension)
        }
        let parts = cleaned.trimmingCharacters(in: .whitespaces).split(separator: ".").map {
            $0.trimmingCharacters(in: .whitespaces).lowercased()
        }
        if parts.count >= 3 {
            let real = parts[parts.count - 1]
            let shown = parts[parts.count - 2]
            if riskByExtension[real] != nil, documentLikeExtensions.contains(shown) {
                reasons.append(.doubleExtension(shown: shown, real: real))
            }
        }
        return reasons
    }

    /// Full assessment. `suggestedName` is the raw name (it may contain the characters that
    /// sanitising removes); `savedName` is the name the file has on disk.
    public static func assess(suggestedName: String, savedName: String) -> Assessment {
        var misleading = misleadingReasons(suggestedName)
        for reason in misleadingReasons(savedName) where !misleading.contains(reason) {
            misleading.append(reason)
        }
        return Assessment(risk: risk(forFileName: savedName), misleading: misleading)
    }

    /// Whether "open safe files after downloading" may open this file.
    public static func isSafeToOpenAutomatically(_ name: String) -> Bool {
        guard misleadingReasons(name).isEmpty, risk(forFileName: name) == nil else { return false }
        return safeToOpenExtensions.contains(DownloadFileName.pathExtension(name))
    }
}
