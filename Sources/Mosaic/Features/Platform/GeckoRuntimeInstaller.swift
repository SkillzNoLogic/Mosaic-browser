import AppKit
import MosaicCore

/// Installs, updates and removes Mosaic's private Gecko runtime (Firefox ESR) from inside
/// the app, so the downloadable build works on a Mac that has never had Firefox.
///
/// 1. Download Firefox ESR's disk image from Mozilla over HTTPS (progress, cancellable;
///    redirects must stay on Mozilla's HTTPS servers).
/// 2. Mount it read-only (`hdiutil attach -nobrowse -readonly`) and copy the app into a
///    staging folder next to its final place.
/// 3. Verify the copy: `codesign --verify --deep --strict`, plus a requirement that it is
///    signed with Mozilla's Developer ID (Team ID 43AQ936H96, `org.mozilla.firefox`), plus an
///    Apple Silicon slice and a Gecko engine inside. Only the verified bytes are installed.
/// 4. Quit the running Mozilla engine if there is one (its profile is separate and kept),
///    move the new app into `<support>/Engines/Firefox.app`, and reopen Mozilla tabs.
@MainActor
final class GeckoRuntimeInstaller: NSObject, ObservableObject {
    @Published private(set) var phase: RuntimeInstallPhase = .idle
    /// Latest Firefox ESR according to Mozilla, after a check.
    @Published private(set) var latestVersion: SoftwareVersion?
    @Published private(set) var lastCheck: Date?
    @Published private(set) var isChecking = false
    /// Result of the last update check or removal, in words.
    @Published private(set) var message: String?

    weak var model: BrowserModel?

    private var work: Task<Void, Never>?
    private var session: URLSession?
    private var downloadContinuation: CheckedContinuation<URL, Error>?
    /// Set when a redirect tried to leave Mozilla's HTTPS servers.
    private var rejectedRedirectHost: String?
    private var downloadDestination: URL?

    private enum Keys {
        static let lastCheck = "geckoRuntimeLastCheck"
        static let latest = "geckoRuntimeLatestESR"
    }

    override init() {
        super.init()
        let defaults = AppPaths.defaults
        lastCheck = defaults.object(forKey: Keys.lastCheck) as? Date
        latestVersion = defaults.string(forKey: Keys.latest).flatMap(SoftwareVersion.init)
    }

    // MARK: - What is installed

    /// Where this profile's private runtime lives.
    var managedAppURL: URL { GeckoRuntime.managedAppURL(applicationSupport: AppPaths.support) }

    /// This profile's private runtime, if installed.
    var installedRuntime: GeckoRuntime? { GeckoRuntime(appURL: managedAppURL, isManaged: true) }

    var installedVersion: SoftwareVersion? { installedRuntime.flatMap { SoftwareVersion($0.version) } }

    var isUpdateAvailable: Bool {
        guard let installedVersion, let latestVersion else { return false }
        return latestVersion > installedVersion
    }

    private var stagingRoot: URL {
        AppPaths.support.appendingPathComponent("Engines/.staging", isDirectory: true)
    }

    // MARK: - Install / update

    /// Installs (or replaces) the private runtime. `source` defaults to Mozilla's latest
    /// ESR; a `file:` URL to a disk image skips the download (used by automated checks).
    func install(from source: URL = FirefoxProductDetails.esrDownloadURL) {
        guard !phase.isActive else { return }
        let previous = work
        message = nil
        transition(.start)
        work = Task { [weak self] in
            await previous?.value  // a cancelled attempt may still be cleaning up
            await self?.runInstall(from: source)
            self?.work = nil
        }
    }

    func cancel() {
        guard phase.isCancellable else { return }
        transition(.cancel)
        session?.invalidateAndCancel()
        work?.cancel()
    }

    func resetMessage() {
        if !phase.isActive { transition(.reset) }
    }

    private func transition(_ event: RuntimeInstallEvent) {
        let next = RuntimeInstallMachine.next(phase, on: event)
        if next != phase { phase = next }
    }

    private var isCancelled: Bool { phase == .cancelled || Task.isCancelled }

    private func runInstall(from source: URL) async {
        let attempt = stagingRoot.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer {
            try? FileManager.default.removeItem(at: attempt)
            try? FileManager.default.removeItem(at: stagingRoot)  // only succeeds once empty
        }
        do {
            // Leftovers of attempts interrupted by a crash or quit.
            try? FileManager.default.removeItem(at: stagingRoot)
            try FileManager.default.createDirectory(at: attempt, withIntermediateDirectories: true)
            let available = try? attempt.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
                .volumeAvailableCapacityForImportantUsage
            guard RuntimeInstallRequirements.hasEnoughSpace(available: available) else {
                throw RuntimeInstallFailure.notEnoughSpace(
                    neededMB: Int(RuntimeInstallRequirements.neededBytes / 1_000_000))
            }

            // 1. Download.
            let image: URL
            if source.isFileURL {
                image = source
                transition(.progress(received: 1, expected: 1))
            } else {
                image = try await download(source, to: attempt.appendingPathComponent("firefox-esr.dmg"))
            }
            guard !isCancelled else { return }
            transition(.downloaded)

            // 2–3. Copy out of the disk image and verify the copy.
            let staged = attempt.appendingPathComponent("Firefox.app", isDirectory: true)
            let version = try await Task.detached(priority: .userInitiated) {
                try await Self.extractAndVerify(image: image, to: staged)
            }.value
            guard !isCancelled else { return }

            // 4. Swap it in, quitting the engine first if it is running.
            let engineRunning = model.map { $0.gecko.status != .stopped && !isFailed($0.gecko.status) } ?? false
            transition(.verified(engineRunning: engineRunning))
            if engineRunning {
                try await stopEngine()
                transition(.engineStopped)
            }
            let destination = managedAppURL
            try await Task.detached(priority: .userInitiated) {
                try Self.replace(destination, with: staged, scratch: attempt)
            }.value
            transition(.installed(version: version))
            engineChanged(reopenTabs: true)
        } catch is CancellationError {
            transition(.cancel)
        } catch let failure as RuntimeInstallFailure {
            if !isCancelled { transition(.fail(failure)) }
        } catch let error as URLError where error.code == .cancelled {
            transition(.cancel)
        } catch let error as URLError {
            if !isCancelled {
                transition(.fail(.from(urlErrorCode: error.errorCode, description: error.localizedDescription)))
            }
        } catch {
            if !isCancelled { transition(.fail(.copyFailed(error.localizedDescription))) }
        }
        session?.invalidateAndCancel()
        session = nil
    }

    private func isFailed(_ status: GeckoEngine.Status) -> Bool {
        if case .failed = status { return true }
        return false
    }

    /// Tells the engine to look for its runtime again and reopens Mozilla tabs that lost
    /// their page (so the new engine is used without relaunching Mosaic).
    private func engineChanged(reopenTabs: Bool) {
        guard let model else { return }
        model.gecko.refreshRuntime()
        model.objectWillChange.send()
        guard reopenTabs, model.gecko.runtime != nil else { return }
        for tab in model.tabs where tab.engine == .gecko && tab.geckoID == nil
            && !tab.isStartPage && tab.internalPage == nil
        {
            model.load(tab.url, in: tab)
        }
    }

    // MARK: Download

    private func download(_ url: URL, to destination: URL) async throws -> URL {
        rejectedRedirectHost = nil
        downloadDestination = destination
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 60
        configuration.waitsForConnectivity = false
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: .main)
        self.session = session
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                downloadContinuation = continuation
                session.downloadTask(with: url).resume()
            }
        } onCancel: {
            session.invalidateAndCancel()
        }
    }

    private func finishDownload(_ result: Result<URL, Error>) {
        guard let continuation = downloadContinuation else { return }
        downloadContinuation = nil
        continuation.resume(with: result)
    }

    // MARK: Verify and install (off the main thread)

    /// Mounts the image, copies the app out, unmounts, and verifies the copy. Returns its version.
    private nonisolated static func extractAndVerify(image: URL, to staged: URL) async throws -> String {
        let mount = FileManager.default.temporaryDirectory
            .appendingPathComponent("mosaic-gecko-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: mount, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: mount) }

        let attach = await ProcessRunner.run(
            "/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-quiet",
                                 "-mountpoint", mount.path, image.path])
        guard attach.status == 0 else { throw RuntimeInstallFailure.damagedDownload }
        do {
            let items = (try? FileManager.default.contentsOfDirectory(at: mount, includingPropertiesForKeys: nil)) ?? []
            let apps = items.filter { $0.pathExtension == "app" }
            guard let app = apps.first(where: { $0.lastPathComponent == "Firefox.app" }) ?? apps.first else {
                throw RuntimeInstallFailure.noAppInDiskImage
            }
            try Task.checkCancellation()
            // ditto copies the bundle exactly as signed (symlinks, permissions). Extended
            // attributes and resource forks are left behind: they are never part of a
            // signature, and stray Finder data would make `codesign --strict` reject the copy.
            let copy = await ProcessRunner.run("/usr/bin/ditto", ["--norsrc", "--noextattr", app.path, staged.path])
            guard copy.status == 0 else {
                throw RuntimeInstallFailure.copyFailed(copy.output.trimmingCharacters(in: .whitespacesAndNewlines))
            }
        } catch {
            await detach(mount)
            throw error
        }
        await detach(mount)
        try Task.checkCancellation()

        // The signature is checked on the copy that will actually run.
        let expectation = SignerExpectation.mozillaFirefox
        let runs = expectation.codesignArguments(appPath: staged.path)
        let integrity = await ProcessRunner.run("/usr/bin/codesign", runs[0])
        try Task.checkCancellation()
        let requirement = await ProcessRunner.run("/usr/bin/codesign", runs[1])
        let display = await ProcessRunner.run("/usr/bin/codesign", runs[2])
        let info = CodeSignatureInfo.parse(codesignDisplay: display.output)
        switch expectation.evaluate(integrityStatus: integrity.status, integrityOutput: integrity.output,
                                    requirementStatus: requirement.status, display: info) {
        case .invalid(let detail): throw RuntimeInstallFailure.signatureInvalid(detail)
        case .wrongSigner(let found): throw RuntimeInstallFailure.wrongSigner(found: found)
        case .trusted: break
        }

        let architectures = Bundle(url: staged)?.executableArchitectures?.map(\.intValue) ?? []
        guard architectures.contains(NSBundleExecutableArchitectureARM64) else {
            throw RuntimeInstallFailure.notAppleSilicon
        }
        guard let runtime = GeckoRuntime(appURL: staged, isManaged: true) else {
            throw RuntimeInstallFailure.notGeckoBrowser
        }
        return runtime.version
    }

    private nonisolated static func detach(_ mount: URL) async {
        let first = await ProcessRunner.run("/usr/bin/hdiutil", ["detach", mount.path, "-quiet"])
        if first.status != 0 {
            _ = await ProcessRunner.run("/usr/bin/hdiutil", ["detach", mount.path, "-quiet", "-force"])
        }
    }

    /// Moves `staged` to `destination`, keeping the old runtime until the new one is in place.
    private nonisolated static func replace(_ destination: URL, with staged: URL, scratch: URL) throws {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        let old = scratch.appendingPathComponent("previous-Firefox.app", isDirectory: true)
        let hadOld = fileManager.fileExists(atPath: destination.path)
        do {
            if hadOld { try fileManager.moveItem(at: destination, to: old) }
            try fileManager.moveItem(at: staged, to: destination)
        } catch {
            if hadOld, !fileManager.fileExists(atPath: destination.path) {
                try? fileManager.moveItem(at: old, to: destination)
            }
            throw RuntimeInstallFailure.copyFailed(error.localizedDescription)
        }
    }

    // MARK: Stopping the engine

    /// Quits Gecko in an orderly way (so its profile is flushed and kept), falling back to
    /// terminating it. Mozilla tabs lose their page and are reopened afterwards.
    private func stopEngine() async throws {
        guard let model else { return }
        let gecko = model.gecko
        let pid = gecko.processIdentifier
        if gecko.isRunning {
            gecko.send("quit")
        } else {
            gecko.stop()
        }
        if let pid {
            if !(await Self.waitForExit(pid, seconds: 8)) {
                gecko.stop()
                guard await Self.waitForExit(pid, seconds: 5) else { throw RuntimeInstallFailure.engineBusy }
            }
        }
        if gecko.status != .stopped { gecko.stop() }
        // Child processes follow the parent within moments.
        try? await Task.sleep(nanoseconds: 500_000_000)
    }

    private static func waitForExit(_ pid: pid_t, seconds: Double) async -> Bool {
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if kill(pid, 0) != 0 { return true }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        return kill(pid, 0) != 0
    }

    // MARK: - Remove

    /// Deletes the private runtime. Mozilla's profile (cookies, extension data) is kept,
    /// so reinstalling picks up where it left off.
    func remove() async {
        guard !phase.isActive, let model else { return }
        let target = managedAppURL
        guard FileManager.default.fileExists(atPath: target.path) else { return }
        message = nil
        do {
            if model.gecko.runtime?.appURL.standardizedFileURL == target.standardizedFileURL,
               model.gecko.status != .stopped
            {
                try await stopEngine()
            }
            let scratch = stagingRoot.appendingPathComponent(UUID().uuidString, isDirectory: true)
            try await Task.detached {
                let fileManager = FileManager.default
                try fileManager.createDirectory(at: scratch, withIntermediateDirectories: true)
                try fileManager.moveItem(at: target, to: scratch.appendingPathComponent("Firefox.app"))
                try fileManager.removeItem(at: scratch)
                try? fileManager.removeItem(at: scratch.deletingLastPathComponent())  // once empty
            }.value
            message = "The Mozilla engine was removed. Its browsing data was kept."
            transition(.reset)
        } catch let failure as RuntimeInstallFailure {
            message = failure.message
        } catch {
            message = RuntimeInstallFailure.removeFailed(error.localizedDescription).message
        }
        engineChanged(reopenTabs: true)
    }

    // MARK: - Update check

    /// Asks Mozilla which Firefox ESR is current.
    func checkForUpdate() async {
        guard !isChecking else { return }
        isChecking = true
        defer { isChecking = false }
        var request = URLRequest(url: FirefoxProductDetails.versionsURL)
        request.timeoutInterval = 20
        request.cachePolicy = .reloadIgnoringLocalCacheData
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard status == 200, let latest = FirefoxProductDetails.esrVersion(from: data) else {
                message = "Mozilla’s version information could not be read (HTTP \(status)). Try again later."
                return
            }
            latestVersion = latest
            lastCheck = Date()
            AppPaths.defaults.set(lastCheck, forKey: Keys.lastCheck)
            AppPaths.defaults.set(latest.original, forKey: Keys.latest)
            if let installedVersion {
                message = latest > installedVersion
                    ? "Firefox ESR \(latest.numberString) is available (installed: \(installedVersion.numberString))."
                    : "The Mozilla engine is up to date (Firefox ESR \(installedVersion.numberString))."
            } else {
                message = "The current Firefox ESR is \(latest.numberString)."
            }
        } catch let error as URLError {
            message = RuntimeInstallFailure.from(urlErrorCode: error.errorCode, description: error.localizedDescription)
                .message
        } catch {
            message = error.localizedDescription
        }
    }
}

// MARK: - URLSession delegate

extension GeckoRuntimeInstaller: URLSessionDownloadDelegate {
    nonisolated func urlSession(
        _ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64
    ) {
        MainActor.assumeIsolated {
            guard session === self.session else { return }
            let expected = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : nil
            transition(.progress(received: totalBytesWritten, expected: expected))
        }
    }

    nonisolated func urlSession(
        _ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL
    ) {
        // The file at `location` is deleted when this returns, so it is moved right here.
        MainActor.assumeIsolated {
            guard session === self.session, let destination = downloadDestination else { return }
            if let host = rejectedRedirectHost {
                finishDownload(.failure(RuntimeInstallFailure.untrustedSource(host)))
                return
            }
            let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
            guard status == 200 else {
                finishDownload(.failure(RuntimeInstallFailure.server(status: status)))
                return
            }
            do {
                try? FileManager.default.removeItem(at: destination)
                try FileManager.default.moveItem(at: location, to: destination)
                finishDownload(.success(destination))
            } catch {
                finishDownload(.failure(RuntimeInstallFailure.copyFailed(error.localizedDescription)))
            }
        }
    }

    nonisolated func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        MainActor.assumeIsolated {
            guard session === self.session, let error else { return }
            if let host = rejectedRedirectHost {
                finishDownload(.failure(RuntimeInstallFailure.untrustedSource(host)))
            } else {
                finishDownload(.failure(error))
            }
        }
    }

    nonisolated func urlSession(
        _ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void
    ) {
        let allowed = request.url.map(FirefoxProductDetails.isTrustedDownloadURL) ?? false
        if allowed {
            completionHandler(request)
            return
        }
        MainActor.assumeIsolated {
            rejectedRedirectHost = request.url?.host ?? request.url?.absoluteString ?? "an unknown server"
        }
        completionHandler(nil)
        task.cancel()
    }
}

/// Runs a command-line tool and collects its combined output.
enum ProcessRunner {
    struct Result: Sendable {
        let status: Int32
        let output: String
    }

    static func run(_ executable: String, _ arguments: [String]) async -> Result {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: executable)
                process.arguments = arguments
                let pipe = Pipe()
                process.standardOutput = pipe
                process.standardError = pipe
                process.standardInput = FileHandle.nullDevice
                do {
                    try process.run()
                } catch {
                    continuation.resume(returning: Result(status: -1, output: error.localizedDescription))
                    return
                }
                let data = pipe.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()
                continuation.resume(returning: Result(
                    status: process.terminationStatus, output: String(decoding: data, as: UTF8.self)))
            }
        }
    }
}
