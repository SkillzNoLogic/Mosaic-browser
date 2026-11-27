import AppKit
import Foundation
import MosaicCore

/// Gecko's side of the download manager: the agent's `configureDownloads`,
/// `downloadsState`, `downloadAction`, `startDownload` and `downloadRequestInfo`.
extension DownloadManager {
    struct GeckoDownload: Decodable {
        let id: Int
        let url: String
        let referrer: String
        let path: String
        let contentType: String
        let totalBytes: Int64
        let currentBytes: Int64
        let speed: Double
        let state: String
        let error: String
        let hasPartialData: Bool
        let restored: Bool
    }

    struct GeckoCommand: Decodable {
        let type: String
        let url: String?
        let referrer: String?
    }

    struct GeckoSnapshot: Decodable {
        let version: Int
        let unchanged: Bool?
        let downloads: [GeckoDownload]?
        let commands: [GeckoCommand]?
    }

    func geckoStatusChanged(_ status: GeckoEngine.Status) {
        if status == .running {
            Task {
                await configureGecko()
                startGeckoPolling()
            }
            return
        }
        geckoPollTask?.cancel()
        geckoPollTask = nil
        geckoWatching = false
        geckoVersion = -1
        // The agent's download IDs start again with a new Gecko process.
        for (_, id) in geckoLinks {
            update(id) {
                guard $0.state.isActive else { return }
                $0.state = .failed
                $0.speed = 0
                $0.errorMessage = "The Mozilla engine stopped"
            }
            if let path = record(id)?.destinationPath { reservedPaths.remove(path) }
        }
        geckoLinks.removeAll()
        ignoredGeckoIDs.removeAll()
    }

    /// Tells Gecko where to save and what to offer; starts watching its downloads.
    func configureGecko() async {
        guard gecko.isRunning else { return }
        let options: [String: Any] = [
            "directory": ensureFolder().path,
            "fdmMenu": fdm.isInstalled && settings.fdmContextMenu,
        ]
        geckoWatching = (try? await gecko.call("configureDownloads", options)) != nil
    }

    func startGeckoPolling() {
        geckoPollTask?.cancel()
        geckoPollTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self, self.gecko.isRunning else { return }
                await self.pollGecko()
                // Twice a second while something is moving, once a second otherwise; an
                // unchanged poll costs one short reply.
                let busy = self.items.contains { $0.engine == .gecko && $0.state == .inProgress }
                    || !self.undecided.isEmpty
                try? await Task.sleep(nanoseconds: busy ? 500_000_000 : 1_000_000_000)
            }
        }
    }

    func pollGecko() async {
        guard let value = try? await gecko.call("downloadsState", geckoVersion) as? [String: Any],
              (value["unchanged"] as? Bool) != true,
              let data = try? JSONSerialization.data(withJSONObject: value),
              let snapshot = try? JSONDecoder().decode(GeckoSnapshot.self, from: data)
        else { return }
        reconcileGecko(snapshot)
    }

    func reconcileGecko(_ snapshot: GeckoSnapshot) {
        geckoVersion = snapshot.version
        for command in snapshot.commands ?? [] {
            switch command.type {
            case "showDownloads":
                if !isScripted { NSApp.activate(ignoringOtherApps: true) }
                openDownloadsPage()
            case "sendLinkToFDM":
                if let url = command.url {
                    sendLinkToFDM(url: url, referrer: command.referrer?.isEmpty == false ? command.referrer : nil, from: .gecko)
                }
            default:
                break
            }
        }

        var seen = Set<Int>()
        for download in snapshot.downloads ?? [] {
            seen.insert(download.id)
            if ignoredGeckoIDs.contains(download.id) { continue }
            if let id = geckoLinks[download.id] {
                applyGecko(download, to: id)
                continue
            }
            // Started by Mosaic itself; `startInGecko` links it when the agent answers.
            if pendingGeckoStarts[download.url] != nil { continue }
            if download.restored {
                // From before this Gecko session: shown again only if it is still listed.
                if let match = items.first(where: {
                    $0.engine == .gecko && $0.url == download.url && $0.destinationPath == download.path
                        && geckoID(of: $0.id) == nil
                }) {
                    geckoLinks[download.id] = match.id
                    applyGecko(download, to: match.id)
                } else {
                    ignoredGeckoIDs.insert(download.id)
                    if ["completed", "failed", "cancelled"].contains(download.state) {
                        gecko.send("downloadAction", download.id, "remove")
                    }
                }
                continue
            }
            adoptGecko(download)
        }

        for (link, id) in geckoLinks where !seen.contains(link) {
            geckoLinks.removeValue(forKey: link)
            update(id) {
                guard $0.state.isActive else { return }
                $0.state = .failed
                $0.speed = 0
                $0.errorMessage = "Removed by the Mozilla engine"
            }
        }
    }

    /// A download a Gecko page started. Gecko is already fetching it into the download
    /// folder; the questions (FDM, duplicate, save as) are asked meanwhile.
    private func adoptGecko(_ download: GeckoDownload) {
        let name = (download.path as NSString).lastPathComponent
        let fileName = name.isEmpty ? URL(string: download.url).map(DownloadFileName.suggested(forURL:)) ?? DownloadFileName.fallback : name
        let record = DownloadRecord(
            url: download.url, referrer: download.referrer.isEmpty ? nil : download.referrer,
            fileName: fileName, suggestedName: fileName,
            destinationPath: download.path.isEmpty ? nil : download.path, engine: .gecko,
            mimeType: download.contentType.isEmpty ? nil : download.contentType,
            totalBytes: download.totalBytes, receivedBytes: download.currentBytes)
        insert(record)
        let id = record.id
        geckoLinks[download.id] = id
        if !download.path.isEmpty { reservedPaths.insert(download.path) }
        undecided.insert(id)
        applyGecko(download, to: id)

        Task {
            let choice = await chooseStart(for: id, isRetry: false)
            undecided.remove(id)
            switch choice {
            case .save(let chosen):
                if let chosen { geckoDestinations[id] = chosen }
                if self.record(id)?.state == .completed { finishCompleted(id) }
            case .cancel:
                dropGeckoDownload(download.id, record: id)
                deleteRecord(id)
            case .handOff:
                dropGeckoDownload(download.id, record: id)
                update(id) { $0.destinationPath = nil; $0.receivedBytes = 0 }
                await handOff(id, from: .gecko)
            case .openExisting(let existing):
                dropGeckoDownload(download.id, record: id)
                deleteRecord(id)
                open(existing.id)
            }
        }
    }

    /// Stops a Gecko download Mosaic does not want, removing what it wrote.
    private func dropGeckoDownload(_ link: Int, record id: UUID) {
        geckoLinks.removeValue(forKey: link)
        ignoredGeckoIDs.insert(link)
        gecko.send("downloadAction", link, "remove")
        guard let record = record(id), let path = record.destinationPath else { return }
        reservedPaths.remove(path)
        // It may have finished while the question was open.
        if record.state == .completed, FileManager.default.fileExists(atPath: path) {
            NSWorkspace.shared.recycle([URL(fileURLWithPath: path)]) { _, _ in }
        }
    }

    private func applyGecko(_ download: GeckoDownload, to id: UUID) {
        let finished = finishedIDs.contains(id)
        update(id) { record in
            guard record.state != .handedOff else { return }
            if download.totalBytes >= 0 { record.totalBytes = download.totalBytes }
            record.receivedBytes = download.currentBytes
            record.speed = download.speed
            if !download.contentType.isEmpty { record.mimeType = download.contentType }
            if !finished, !download.path.isEmpty {
                record.destinationPath = download.path
                record.fileName = (download.path as NSString).lastPathComponent
            }
            switch download.state {
            case "inProgress":
                record.state = .inProgress
                record.errorMessage = nil
                record.canResume = false
                record.endedAt = nil
            case "paused":
                record.state = .paused
                record.speed = 0
                record.canResume = true
            case "completed":
                record.state = .completed
                record.speed = 0
                record.endedAt = record.endedAt ?? Date()
            case "failed":
                record.state = .failed
                record.speed = 0
                record.errorMessage = download.error.isEmpty ? "Download failed" : download.error
                record.canResume = download.hasPartialData
                record.endedAt = Date()
            case "cancelled":
                record.state = .cancelled
                record.speed = 0
                record.errorMessage = nil
                record.canResume = false
                record.endedAt = Date()
            default:
                break
            }
        }
        switch download.state {
        case "completed":
            finishCompleted(id)
        case "failed", "cancelled":
            reservedPaths.remove(download.path)
        default:
            break
        }
    }
}
