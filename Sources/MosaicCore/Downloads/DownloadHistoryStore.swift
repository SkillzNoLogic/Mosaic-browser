import Foundation

/// The download history, kept as one JSON file in the profile folder.
///
/// Newest first. Not thread-safe: use it from one actor (the app uses the main actor).
public final class DownloadHistoryStore {
    public let fileURL: URL
    /// Oldest finished entries beyond this are dropped when saving.
    public let limit: Int
    public private(set) var records: [DownloadRecord] = []

    /// What a download that was running when Mosaic quit is marked with on the next launch.
    public static let interruptedMessage = "Interrupted when Mosaic quit"

    private struct File: Codable {
        var version = 1
        var downloads: [DownloadRecord]
    }

    public init(fileURL: URL, limit: Int = 1000) {
        self.fileURL = fileURL
        self.limit = limit
    }

    /// Reads the file. A download that was still running (or paused) when Mosaic last quit
    /// cannot be continued by the engine any more, so it is marked failed and can be retried.
    /// A missing or unreadable file gives an empty history (an unreadable one is kept aside
    /// as `<name>.corrupt` rather than overwritten).
    public func load() {
        guard let data = try? Data(contentsOf: fileURL) else {
            records = []
            return
        }
        do {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            var loaded = try decoder.decode(File.self, from: data).downloads
            for index in loaded.indices where loaded[index].state.isActive {
                loaded[index].state = .failed
                loaded[index].errorMessage = Self.interruptedMessage
                loaded[index].speed = 0
            }
            records = loaded.sorted { $0.startedAt > $1.startedAt }
        } catch {
            let aside = fileURL.appendingPathExtension("corrupt")
            try? FileManager.default.removeItem(at: aside)
            try? FileManager.default.moveItem(at: fileURL, to: aside)
            records = []
        }
    }

    /// Writes the file atomically.
    public func save() throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try encoder.encode(File(downloads: records)).write(to: fileURL, options: .atomic)
    }

    public func record(_ id: UUID) -> DownloadRecord? { records.first { $0.id == id } }

    /// Adds `record`, or replaces the one with the same ID (keeping its place).
    public func upsert(_ record: DownloadRecord) {
        if let index = records.firstIndex(where: { $0.id == record.id }) {
            records[index] = record
        } else {
            records.insert(record, at: 0)
            trim()
        }
    }

    public func remove(_ id: UUID) {
        records.removeAll { $0.id == id }
    }

    /// Removes every finished entry; downloads still running or paused stay.
    public func clearFinished() {
        records.removeAll { !$0.state.isActive }
    }

    /// Entries matching `query` (see `DownloadRecord.matches`), newest first.
    public func search(_ query: String) -> [DownloadRecord] {
        records.filter { $0.matches(query) }
    }

    /// The most recent completed download of `url` whose file is still there, for the
    /// duplicate-download warning. `fileExists` is injectable for tests.
    public func completedDownload(
        of url: String, excluding id: UUID? = nil,
        fileExists: (String) -> Bool = { FileManager.default.fileExists(atPath: $0) }
    ) -> DownloadRecord? {
        let key = Self.normalised(url)
        return records.first { record in
            record.id != id && record.state == .completed
                && (Self.normalised(record.url) == key || record.originalURL.map(Self.normalised) == key)
                && record.destinationPath.map(fileExists) == true
        }
    }

    /// URLs compared without their fragment.
    static func normalised(_ url: String) -> String {
        guard var components = URLComponents(string: url) else { return url }
        components.fragment = nil
        return components.string ?? url
    }

    private func trim() {
        guard records.count > limit else { return }
        // Drop the oldest finished entries first; never an active download.
        var excess = records.count - limit
        for index in records.indices.reversed() where excess > 0 && !records[index].state.isActive {
            records.remove(at: index)
            excess -= 1
        }
    }
}
