import Foundation
import MosaicCore

/// Turns the stream of tab changes from both engines into one history visit per
/// navigation, recorded once the page has settled so the visit carries its final title.
///
/// The model reports every change of every tab (selected or not, Gecko or Chromium) through
/// `Library.tabDidChange`. A visit is committed when a tab has finished loading a new
/// address and its title has been stable for a moment, or straight away when the tab moves
/// on before that. Reloads of the same page and title changes do not add visits; a title
/// that changes later only updates the stored title.
@MainActor
final class HistoryRecorder {
    struct Visit: Equatable {
        var url: String
        var title: String
        var typed: Bool
    }

    /// How long a finished page's title must stay unchanged before its visit is recorded.
    var settleDelay: TimeInterval = 1.5
    /// A page whose title keeps changing is recorded after this long anyway.
    var maximumDelay: TimeInterval = 4

    private let commit: (Visit) -> Void
    private let retitle: (_ url: String, _ title: String) -> Void

    private struct Track {
        var loadRequest: Date
        var typedRequest: Date?
        var lastRecordedURL: String?
        var recordedTitle = ""
        var pending: Visit?
        var pendingSince = Date()
        var timer: Task<Void, Never>?
    }

    private var tracks: [UUID: Track] = [:]

    init(commit: @escaping (Visit) -> Void, retitle: @escaping (String, String) -> Void) {
        self.commit = commit
        self.retitle = retitle
    }

    /// Error pages keep the failed address, so they are recognised by their titles: Mosaic's
    /// own Chromium error page, and Gecko's network and certificate error pages.
    static let errorTitles: Set<String> = [
        "Can’t open this page",
        "Problem loading page", "Server Not Found", "Hmm. We’re having trouble finding that site.",
        "Unable to connect", "The connection has timed out", "Secure Connection Failed",
        "Warning: Potential Security Risk Ahead", "Did Not Connect: Potential Security Issue",
        "Page Not Found", "Offline mode", "Server not found",
    ]

    /// One report about a tab. `excluded` tabs (private, internal pages, failures) never record.
    func observe(id: UUID, url: String, title: String, isLoading: Bool, loadRequest: Date, excluded: Bool) {
        var track = tracks[id] ?? Track(loadRequest: loadRequest)
        defer { tracks[id] = track }

        // An explicit load (typed, a bookmark, a link opened in a new tab): whatever was
        // pending is a finished visit, and the same address may be visited again.
        if track.loadRequest != loadRequest {
            flush(&track)
            track.loadRequest = loadRequest
            track.lastRecordedURL = nil
        }
        let isError = Self.errorTitles.contains(title)
        // The page rewrote its address on the same site while its visit was still settling
        // (`history.replaceState` or a script redirect right after loading; Chromium reports
        // these as a brief load): one visit, at the final address.
        if var pending = track.pending, pending.url != url, !excluded, !isError, LibraryURL.isRecordable(url),
           LibraryURL.host(of: url) == LibraryURL.host(of: pending.url),
           Date().timeIntervalSince(track.pendingSince) < settleDelay {
            pending.url = url
            pending.title = title
            track.pending = pending
            schedule(id, &track)
            return
        }
        if let pending = track.pending, pending.url != url || excluded || isError {
            // Moved on before the page settled (or it turned out to be an error page).
            if isError, pending.url == url { cancelPending(&track) } else { flush(&track) }
        }
        guard !excluded, !isError, LibraryURL.isRecordable(url) else { return }
        guard !isLoading else { return }

        if url == track.lastRecordedURL {
            if !title.isEmpty, title != track.recordedTitle {
                track.recordedTitle = title
                retitle(url, title)
            }
            return
        }
        if var pending = track.pending {
            guard pending.title != title else { return }
            pending.title = title
            track.pending = pending
        } else {
            let typed = track.typedRequest == track.loadRequest && track.lastRecordedURL == nil
            track.pending = Visit(url: url, title: title, typed: typed)
            track.pendingSince = Date()
        }
        schedule(id, &track)
    }

    /// The model's last load in this tab came from the address bar.
    func noteTyped(id: UUID, loadRequest: Date) {
        var track = tracks[id] ?? Track(loadRequest: loadRequest)
        track.typedRequest = loadRequest
        tracks[id] = track
    }

    /// Commits every pending visit now (on quit).
    func flushAll() {
        for id in Array(tracks.keys) {
            guard var track = tracks[id] else { continue }
            flush(&track)
            tracks[id] = track
        }
    }

    /// Drops bookkeeping for tabs that no longer exist (their pending visits commit first).
    func prune(keeping live: Set<UUID>) {
        for (id, var track) in tracks where !live.contains(id) {
            flush(&track)
            tracks[id] = nil
        }
    }

    private func schedule(_ id: UUID, _ track: inout Track) {
        track.timer?.cancel()
        let elapsed = Date().timeIntervalSince(track.pendingSince)
        let delay = max(0, min(settleDelay, maximumDelay - elapsed))
        track.timer = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            guard !Task.isCancelled, let self, var current = self.tracks[id] else { return }
            self.flush(&current)
            self.tracks[id] = current
        }
    }

    private func flush(_ track: inout Track) {
        track.timer?.cancel()
        track.timer = nil
        guard let visit = track.pending else { return }
        track.pending = nil
        track.lastRecordedURL = visit.url
        track.recordedTitle = visit.title
        commit(visit)
    }

    private func cancelPending(_ track: inout Track) {
        track.timer?.cancel()
        track.timer = nil
        track.pending = nil
    }
}
