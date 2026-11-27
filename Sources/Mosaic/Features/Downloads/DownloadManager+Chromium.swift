import CEFBridge
import Foundation
import MosaicCore

/// Chromium's side of the download manager (see CEFDownloads.mm).
extension DownloadManager {
    /// Chromium is about to write a download: decide where (or whether) it goes.
    func chromiumWillStart(_ download: MCEFDownload, decide: @escaping (String?) -> Void) {
        // Chromium asks again for a download it is resuming after an interruption: it is
        // the same entry, continued at the path already chosen.
        if let existing = chromiumLinks[download.identifier], let record = record(existing) {
            if let path = record.destinationPath, !undecided.contains(existing) {
                update(existing) { $0.state = .inProgress; $0.errorMessage = nil }
                decide(path)
            } else if !undecided.contains(existing) {
                decide(defaultDestination(fileName: record.fileName, mimeType: record.mimeType).path)
            }
            return
        }
        let suggested: String
        if !download.suggestedName.isEmpty {
            suggested = download.suggestedName
        } else if let url = URL(string: download.url) {
            suggested = DownloadFileName.suggested(forURL: url)
        } else {
            suggested = DownloadFileName.fallback
        }
        let fileName = DownloadFileName.sanitize(suggested)
        let original = download.originalURL.isEmpty || download.originalURL == download.url ? nil : download.originalURL
        let retryID = pendingRetries.removeValue(forKey: download.url)
            ?? original.flatMap { pendingRetries.removeValue(forKey: $0) }

        let id: UUID
        if let retryID, record(retryID) != nil {
            id = retryID
            update(retryID) {
                $0.engine = .chromium
                $0.state = .inProgress
                $0.errorMessage = nil
                $0.receivedBytes = 0
                $0.totalBytes = download.totalBytes
                $0.endedAt = nil
            }
        } else {
            let record = DownloadRecord(
                url: download.url, originalURL: original,
                referrer: download.pageURL.isEmpty ? nil : download.pageURL,
                fileName: fileName, suggestedName: suggested, engine: .chromium,
                mimeType: download.mimeType.isEmpty ? nil : download.mimeType,
                totalBytes: download.totalBytes)
            insert(record)
            id = record.id
        }
        chromiumLinks[download.identifier] = id
        undecided.insert(id)

        Task {
            let choice = await chooseStart(for: id, isRetry: retryID != nil)
            undecided.remove(id)
            guard let record = record(id) else { return decide(nil) }
            switch choice {
            case .save(let chosen):
                let destination: URL
                if let chosen {
                    destination = chosen
                } else if retryID != nil, let previous = record.destinationPath,
                          !FileManager.default.fileExists(atPath: previous)
                {
                    destination = URL(fileURLWithPath: previous)
                } else {
                    destination = defaultDestination(fileName: record.fileName, mimeType: record.mimeType)
                }
                try? FileManager.default.createDirectory(
                    at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
                reservedPaths.insert(destination.path)
                update(id) {
                    $0.destinationPath = destination.path
                    $0.fileName = destination.lastPathComponent
                }
                decide(destination.path)
            case .cancel:
                chromiumLinks.removeValue(forKey: download.identifier)
                decide(nil)
                if retryID != nil {
                    update(id) { $0.state = .cancelled; $0.endedAt = Date() }
                } else {
                    deleteRecord(id)
                }
            case .handOff:
                // Nothing was written: Chromium drops the download when it gets no path.
                chromiumLinks.removeValue(forKey: download.identifier)
                decide(nil)
                await handOff(id, from: .chromium)
            case .openExisting(let existing):
                chromiumLinks.removeValue(forKey: download.identifier)
                decide(nil)
                deleteRecord(id)
                open(existing.id)
            }
        }
    }

    func chromiumDidUpdate(_ download: MCEFDownload) {
        guard let id = chromiumLinks[download.identifier], record(id) != nil else { return }
        if undecided.contains(id) {
            // Still asking; Chromium may already be fetching into a temporary file.
            update(id) { if download.totalBytes > 0 { $0.totalBytes = download.totalBytes } }
            return
        }
        update(id) { record in
            if download.totalBytes > 0 { record.totalBytes = download.totalBytes }
            record.receivedBytes = download.receivedBytes
            record.speed = Double(download.speed)
            switch download.state {
            case .inProgress:
                record.state = .inProgress
                record.canResume = false
                record.errorMessage = nil
            case .paused:
                record.state = .paused
                record.speed = 0
                record.canResume = true
            case .complete:
                record.state = .completed
                record.speed = 0
                record.endedAt = Date()
                // While downloading, the path is Chromium's temporary file; now it is final.
                if !download.fullPath.isEmpty {
                    record.destinationPath = download.fullPath
                    record.fileName = (download.fullPath as NSString).lastPathComponent
                }
            case .cancelled:
                record.state = .cancelled
                record.speed = 0
                record.endedAt = Date()
                record.canResume = false
            case .interrupted:
                record.state = .failed
                record.speed = 0
                record.errorMessage = download.interruptDescription
                // Chromium can try to resume an interrupted download (the server decides).
                record.canResume = true
            @unknown default:
                break
            }
        }
        switch download.state {
        case .complete:
            chromiumLinks.removeValue(forKey: download.identifier)
            finishCompleted(id)
        case .cancelled:
            chromiumLinks.removeValue(forKey: download.identifier)
            if let path = record(id)?.destinationPath { reservedPaths.remove(path) }
        default:
            break
        }
    }
}
