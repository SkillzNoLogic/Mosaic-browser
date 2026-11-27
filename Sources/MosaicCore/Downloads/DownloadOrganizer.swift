import Foundation

/// Kinds of files, for sorting downloads into subfolders.
public enum DownloadFileCategory: String, Codable, CaseIterable, Sendable, Identifiable {
    case images, documents, audio, video, archives, installers

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .images: "Images"
        case .documents: "Documents"
        case .audio: "Audio"
        case .video: "Video"
        case .archives: "Archives"
        case .installers: "Apps & Installers"
        }
    }

    /// Default name of the subfolder.
    public var defaultFolder: String {
        switch self {
        case .images: "Images"
        case .documents: "Documents"
        case .audio: "Music"
        case .video: "Videos"
        case .archives: "Archives"
        case .installers: "Installers"
        }
    }

    public var symbol: String {
        switch self {
        case .images: "photo"
        case .documents: "doc.text"
        case .audio: "music.note"
        case .video: "film"
        case .archives: "archivebox"
        case .installers: "shippingbox"
        }
    }

    /// A few examples, for the settings page.
    public var examples: String {
        switch self {
        case .images: "JPEG, PNG, HEIC, GIF, SVG"
        case .documents: "PDF, Word, Excel, Pages, text"
        case .audio: "MP3, AAC, WAV, FLAC"
        case .video: "MP4, MOV, MKV, WebM"
        case .archives: "ZIP, 7z, RAR, tar.gz"
        case .installers: "DMG, PKG, apps"
        }
    }

    static let byExtension: [String: DownloadFileCategory] = {
        var map: [String: DownloadFileCategory] = [:]
        let table: [DownloadFileCategory: [String]] = [
            .images: ["jpg", "jpeg", "png", "gif", "heic", "heif", "webp", "tif", "tiff", "bmp", "svg", "ico", "raw",
                      "cr2", "nef", "arw", "dng", "avif", "psd"],
            .documents: ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "md", "csv", "odt", "ods",
                         "odp", "pages", "numbers", "key", "epub", "tex", "json", "xml"],
            .audio: ["mp3", "m4a", "aac", "wav", "aif", "aiff", "flac", "ogg", "opus", "wma", "alac", "mid", "midi"],
            .video: ["mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "3gp", "ts"],
            .archives: ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "lz", "z", "cab", "sit", "sitx"],
            .installers: ["dmg", "pkg", "mpkg", "app", "iso", "img", "exe", "msi", "apk", "ipa", "deb", "rpm", "appimage"],
        ]
        for (category, extensions) in table {
            for ext in extensions { map[ext] = category }
        }
        return map
    }()

    /// The category of a file, from its extension or, failing that, its MIME type.
    public static func of(fileName: String, mimeType: String? = nil) -> DownloadFileCategory? {
        if let category = byExtension[DownloadFileName.pathExtension(fileName)] { return category }
        guard let mime = mimeType?.lowercased().split(separator: ";").first.map(String.init) else { return nil }
        if mime.hasPrefix("image/") { return .images }
        if mime.hasPrefix("audio/") { return .audio }
        if mime.hasPrefix("video/") { return .video }
        if mime.hasPrefix("text/") || mime == "application/pdf" || mime.contains("officedocument")
            || mime == "application/msword" { return .documents }
        if ["application/zip", "application/x-7z-compressed", "application/x-rar-compressed", "application/gzip",
            "application/x-tar", "application/x-bzip2", "application/x-xz"].contains(mime) { return .archives }
        if ["application/x-apple-diskimage", "application/vnd.apple.installer+xml"].contains(mime) { return .installers }
        return nil
    }
}

/// Sorting downloads into subfolders of the download folder by kind of file.
///
/// Off by default. When on, each enabled category goes into its folder (created as needed);
/// files of other kinds, and files the user placed explicitly with "ask where to save",
/// stay where they were saved.
public struct DownloadOrganizationRules: Codable, Hashable, Sendable {
    public var isEnabled: Bool
    /// Folder name per category; a category missing here is not sorted.
    public var folders: [DownloadFileCategory: String]

    public init(isEnabled: Bool = false, folders: [DownloadFileCategory: String]? = nil) {
        self.isEnabled = isEnabled
        self.folders = folders ?? Dictionary(uniqueKeysWithValues: DownloadFileCategory.allCases.map { ($0, $0.defaultFolder) })
    }

    /// The subfolder (a single, sanitised path component) for a file, or nil to leave it
    /// directly in the download folder.
    public func subfolder(forFileName name: String, mimeType: String? = nil) -> String? {
        guard isEnabled, let category = DownloadFileCategory.of(fileName: name, mimeType: mimeType),
              let folder = folders[category] else { return nil }
        let clean = DownloadFileName.sanitize(folder)
        guard !folder.trimmingCharacters(in: .whitespaces).isEmpty, clean != DownloadFileName.fallback || folder == clean
        else { return nil }
        return clean
    }

    /// Where a file named `name` belongs, given the download folder.
    public func directory(forFileName name: String, mimeType: String? = nil, in downloads: URL) -> URL {
        guard let folder = subfolder(forFileName: name, mimeType: mimeType) else { return downloads }
        return downloads.appendingPathComponent(folder, isDirectory: true)
    }
}
