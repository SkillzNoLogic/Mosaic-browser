import Foundation

/// Where a download is in its life.
public enum DownloadState: String, Codable, Sendable, CaseIterable {
    case inProgress, paused, completed, failed, cancelled
    /// Handed to Free Download Manager; Mosaic's engine did not download it.
    case handedOff

    public var isActive: Bool { self == .inProgress || self == .paused }

    public var title: String {
        switch self {
        case .inProgress: "Downloading"
        case .paused: "Paused"
        case .completed: "Completed"
        case .failed: "Failed"
        case .cancelled: "Cancelled"
        case .handedOff: "Sent to Free Download Manager"
        }
    }
}

/// Who did (or is doing) the downloading.
public enum DownloadEngine: String, Codable, Sendable {
    case chromium, gecko
    /// Free Download Manager.
    case fdm

    public var displayName: String {
        switch self {
        case .chromium: "Chromium"
        case .gecko: "Mozilla"
        case .fdm: "Free Download Manager"
        }
    }
}

/// One download, as listed on the Downloads page and kept in the download history.
public struct DownloadRecord: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    /// The URL the file came from (after redirects, when the engine reports them).
    public var url: String
    /// The URL that was originally requested, if different.
    public var originalURL: String?
    /// The page the download was started from.
    public var referrer: String?
    /// The file's name on disk (or the name it will have).
    public var fileName: String
    /// The name the server or page suggested, before sanitising; kept for safety checks.
    public var suggestedName: String?
    /// Full path of the file once Mosaic knows it.
    public var destinationPath: String?
    public var engine: DownloadEngine
    public var mimeType: String?
    /// Total size in bytes; -1 when the server did not say.
    public var totalBytes: Int64
    public var receivedBytes: Int64
    public var state: DownloadState
    public var errorMessage: String?
    public var startedAt: Date
    public var endedAt: Date?
    /// The user saw the safety warning for this file and chose to keep it.
    public var warningAcknowledged: Bool

    // Live values, not persisted.
    /// Bytes per second while downloading.
    public var speed: Double = 0
    /// The engine can continue this download from where it stopped.
    public var canResume = false

    enum CodingKeys: String, CodingKey {
        case id, url, originalURL, referrer, fileName, suggestedName, destinationPath, engine, mimeType
        case totalBytes, receivedBytes, state, errorMessage, startedAt, endedAt, warningAcknowledged
    }

    public init(
        id: UUID = UUID(), url: String, originalURL: String? = nil, referrer: String? = nil, fileName: String,
        suggestedName: String? = nil, destinationPath: String? = nil, engine: DownloadEngine, mimeType: String? = nil,
        totalBytes: Int64 = -1, receivedBytes: Int64 = 0, state: DownloadState = .inProgress,
        errorMessage: String? = nil, startedAt: Date = Date(), endedAt: Date? = nil, warningAcknowledged: Bool = false
    ) {
        self.id = id
        self.url = url
        self.originalURL = originalURL
        self.referrer = referrer
        self.fileName = fileName
        self.suggestedName = suggestedName
        self.destinationPath = destinationPath
        self.engine = engine
        self.mimeType = mimeType
        self.totalBytes = totalBytes
        self.receivedBytes = receivedBytes
        self.state = state
        self.errorMessage = errorMessage
        self.startedAt = startedAt
        self.endedAt = endedAt
        self.warningAcknowledged = warningAcknowledged
    }

    /// 0…1 when the size is known.
    public var fractionCompleted: Double? {
        if state == .completed { return 1 }
        guard totalBytes > 0 else { return nil }
        return min(1, max(0, Double(receivedBytes) / Double(totalBytes)))
    }

    /// Seconds left at the current speed, when that can be estimated.
    public var secondsRemaining: Double? {
        guard state == .inProgress, totalBytes > 0, speed > 1 else { return nil }
        return Double(max(0, totalBytes - receivedBytes)) / speed
    }

    /// Host of the source, for display and search.
    public var host: String? { URL(string: url)?.host }

    /// Safety assessment of the file's name.
    public var assessment: DownloadSafety.Assessment {
        DownloadSafety.assess(suggestedName: suggestedName ?? fileName, savedName: fileName)
    }

    /// A completed file that still needs the user to confirm they want to keep or open it.
    public var needsSafetyConfirmation: Bool { !warningAcknowledged && assessment.needsWarning }

    /// Whether `query` (space-separated terms, all required) matches this record's name,
    /// address, host or folder. Case- and diacritic-insensitive.
    public func matches(_ query: String) -> Bool {
        let terms = query.split(whereSeparator: \.isWhitespace)
        guard !terms.isEmpty else { return true }
        let haystack = [fileName, url, originalURL ?? "", referrer ?? "", destinationPath ?? "", host ?? ""]
            .joined(separator: "\n")
        return terms.allSatisfy { haystack.range(of: $0, options: [.caseInsensitive, .diacriticInsensitive]) != nil }
    }
}
