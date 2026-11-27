import AppKit
import MosaicCore

/// Free Download Manager (FDM 6, `org.freedownloadmanager.fdm6`), when it is installed.
///
/// Downloads are handed over through FDM's own browser-integration helper, `wenativehost`
/// (the program FDM's Chrome and Firefox extensions talk to), which accepts the URL plus
/// the referrer, the User-Agent and, if the user allows it, cookies. FDM then shows its
/// "New download" window, where the user confirms. If the helper is missing or does not
/// answer, Mosaic opens the URL with FDM instead (FDM registers for http/https/ftp), which
/// passes the address only. Mosaic never changes FDM's settings.
@MainActor
final class FreeDownloadManager: ObservableObject {
    static let bundleIdentifier = "org.freedownloadmanager.fdm6"

    @Published private(set) var appURL: URL?
    @Published private(set) var version: String?

    var isInstalled: Bool { appURL != nil }

    init() {
        refresh()
    }

    /// Looks FDM up again (it may have been installed or removed while Mosaic runs).
    func refresh() {
        let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: Self.bundleIdentifier)
        if url != appURL { appURL = url }
        let found = url.flatMap { Bundle(url: $0)?.infoDictionary?["CFBundleShortVersionString"] as? String }
        if found != version { version = found }
    }

    private var nativeHost: URL? {
        guard let appURL else { return nil }
        let host = appURL.appendingPathComponent("Contents/MacOS/wenativehost")
        return FileManager.default.isExecutableFile(atPath: host.path) ? host : nil
    }

    enum HandoffError: LocalizedError {
        case notInstalled
        case openFailed(String)

        var errorDescription: String? {
            switch self {
            case .notInstalled: "Free Download Manager is not installed."
            case .openFailed(let reason): "Free Download Manager could not be opened: \(reason)"
            }
        }
    }

    /// How the download reached FDM.
    enum Route: String {
        /// Through FDM's helper, with referrer/User-Agent (and cookies if allowed).
        case integration
        /// By opening the URL with FDM; the address only.
        case openURL
    }

    /// Hands `request` to FDM. Returns once FDM has it; `onOutcome` is called later, when
    /// the user confirms or cancels in FDM (only for the `.integration` route).
    /// `bringToFront` activates FDM so its confirmation window is visible.
    func send(
        _ request: FDMDownloadRequest, bringToFront: Bool,
        onOutcome: @escaping @MainActor (FDMNativeMessage.Outcome) -> Void
    ) async throws -> Route {
        refresh()
        guard let appURL else { throw HandoffError.notInstalled }
        if let host = nativeHost {
            let session = FDMHostSession(executable: host)
            if await session.deliver(request) {
                if bringToFront { activateWhenRunning() }
                Task { @MainActor in
                    if let outcome = await session.waitForOutcome() { onOutcome(outcome) }
                }
                return .integration
            }
            session.close()
        }
        guard let url = URL(string: request.url) else { throw HandoffError.openFailed("invalid address") }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = bringToFront
        configuration.addsToRecentItems = false
        do {
            _ = try await NSWorkspace.shared.open([url], withApplicationAt: appURL, configuration: configuration)
        } catch {
            throw HandoffError.openFailed(error.localizedDescription)
        }
        return .openURL
    }

    /// FDM's helper starts FDM in the background when it is not running; bring it forward
    /// once it is, so its "New download" window can be seen.
    private func activateWhenRunning() {
        Task { @MainActor in
            for _ in 0..<20 {
                if let app = NSRunningApplication.runningApplications(withBundleIdentifier: Self.bundleIdentifier).first,
                   app.isFinishedLaunching
                {
                    app.activate()
                    return
                }
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
    }
}

/// One conversation with `wenativehost` over its standard input and output.
@MainActor
private final class FDMHostSession {
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private var buffer = Data()
    private var waiters: [String: CheckedContinuation<[String: Any]?, Never>] = [:]
    private var received: [String: [String: Any]] = [:]
    private var closed = false
    /// The helper exited or closed its output.
    private var ended = false

    init(executable: URL) {
        process.executableURL = executable
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        // A write after the helper exits must fail, not raise SIGPIPE in Mosaic.
        _ = fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let chunk = handle.availableData
            Task { @MainActor in self?.receive(chunk) }
        }
        process.terminationHandler = { [weak self] _ in
            Task { @MainActor in self?.finish() }
        }
    }

    /// Handshake, then the download. True when FDM's helper took it.
    func deliver(_ request: FDMDownloadRequest) async -> Bool {
        do {
            try process.run()
        } catch {
            return false
        }
        guard write(FDMNativeMessage.handshake(id: "1")),
              let reply = await response(to: "1", timeout: 8), (reply["error"] as? String ?? "").isEmpty,
              write(FDMNativeMessage.createDownloads(id: "2", [request]))
        else { return false }
        return true
    }

    /// FDM's answer to the download: after the user confirms or cancels (or at once if FDM
    /// cannot fetch it). Gives up after ten minutes.
    func waitForOutcome() async -> FDMNativeMessage.Outcome? {
        defer { close() }
        guard let reply = await response(to: "2", timeout: 600) else { return nil }
        return FDMNativeMessage.outcome(of: reply)
    }

    func close() {
        guard !closed else { return }
        closed = true
        output.fileHandleForReading.readabilityHandler = nil
        try? input.fileHandleForWriting.close()
        if process.isRunning { process.terminate() }
        finish()
    }

    private func write(_ message: [String: Any]) -> Bool {
        guard !closed, !ended, let data = try? FDMNativeMessage.frame(message) else { return false }
        do {
            try input.fileHandleForWriting.write(contentsOf: data)
            return true
        } catch {
            return false
        }
    }

    private func response(to id: String, timeout: Double) async -> [String: Any]? {
        if let message = received.removeValue(forKey: id) { return message }
        if closed || ended { return nil }
        return await withCheckedContinuation { continuation in
            waiters[id] = continuation
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                self.waiters.removeValue(forKey: id)?.resume(returning: nil)
            }
        }
    }

    private func receive(_ chunk: Data) {
        guard !chunk.isEmpty else { return finish() }
        buffer.append(chunk)
        for message in FDMNativeMessage.takeMessages(from: &buffer) {
            // Requests from the helper (cookie queries) are declined: Mosaic passes cookies
            // only with the download itself, and only when the user allowed it.
            if (message["isResponse"] as? Bool) == false {
                if let id = message["id"] {
                    _ = write(["id": id, "type": message["type"] ?? "", "error": "not_implemented", "isResponse": true])
                }
                continue
            }
            guard let id = message["id"] as? String else { continue }
            if let waiter = waiters.removeValue(forKey: id) {
                waiter.resume(returning: message)
            } else {
                received[id] = message
            }
        }
    }

    private func finish() {
        ended = true
        for (_, waiter) in waiters { waiter.resume(returning: nil) }
        waiters.removeAll()
    }
}
