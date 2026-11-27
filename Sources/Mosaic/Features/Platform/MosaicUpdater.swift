import AppKit
import CryptoKit
import MosaicCore
import Security

/// Downloads, verifies and installs new Mosaic releases (Settings › About & Updates).
///
/// 1. `UpdateChecker` finds a newer release on GitHub.
/// 2. `download(_:)` fetches the release's zip and its `.sig`, checks the Ed25519 signature
///    against Mosaic's release key (`UpdatePackage`), unpacks the app, checks its code
///    signature, bundle identifier and version, and keeps it in Application Support.
/// 3. The update is installed when Mosaic quits, or at once with `restartToUpdate()`: a
///    small script waits for Mosaic to exit, swaps the app bundle (restoring the old one if
///    anything fails) and, for a restart, opens the new version.
///
/// With automatic updates on, a newly found release is downloaded in the background and
/// installed the next time Mosaic quits.
@MainActor
final class MosaicUpdater: ObservableObject {
    enum Phase: Equatable {
        case idle
        case downloading(version: String, fraction: Double?)
        case verifying(version: String)
        /// Downloaded and verified; installs at quit or on Restart to Update.
        case ready(version: String)
        case failed(String)

        var isBusy: Bool {
            switch self {
            case .downloading, .verifying: true
            default: false
            }
        }
    }

    @Published private(set) var phase: Phase = .idle
    /// Download and install new releases without asking.
    @Published var automatic: Bool {
        didSet {
            AppPaths.defaults.set(automatic, forKey: Keys.automatic)
            if automatic, let checker, checker.frequency == .manual { checker.frequency = .daily }
        }
    }
    /// Whether a GitHub token is saved (needed while the repository is private).
    @Published private(set) var hasToken: Bool
    @Published private(set) var tokenError: String?

    /// Why this copy of Mosaic cannot replace itself, or nil if it can.
    let locationProblem: UpdatePackage.LocationProblem?

    weak var checker: UpdateChecker?
    private var stagedApp: URL?
    private var downloadTask: Task<Void, Never>?
    private var relaunchAfterInstall = false
    private var installerLaunched = false

    private enum Keys {
        static let automatic = "updateAutoInstall"
    }

    private let token: GitHubTokenStore

    init() {
        token = GitHubTokenStore()
        automatic = AppPaths.defaults.bool(forKey: Keys.automatic)
        hasToken = token.read() != nil
        locationProblem = Self.assessLocation(Bundle.main.bundleURL)
        // Leftovers from an update that was never installed: fetched again when needed.
        try? FileManager.default.removeItem(at: Self.stagingRoot)
    }

    static var stagingRoot: URL { AppPaths.support.appendingPathComponent("Updates", isDirectory: true) }

    private static func assessLocation(_ app: URL) -> UpdatePackage.LocationProblem? {
        let values = try? app.resourceValues(forKeys: [.volumeIsReadOnlyKey])
        let parent = app.deletingLastPathComponent().path
        return UpdatePackage.locationProblem(
            appPath: app.path, volumeIsReadOnly: values?.volumeIsReadOnly ?? false,
            parentIsWritable: FileManager.default.isWritableFile(atPath: parent))
    }

    // MARK: Token

    /// Adds the saved GitHub token to a request for GitHub's API.
    func authorize(_ request: inout URLRequest) {
        guard let token = token.read(), request.url?.host?.lowercased() == "api.github.com" else { return }
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
    }

    func saveToken(_ value: String) {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        tokenError = token.write(trimmed)
        hasToken = token.read() != nil
    }

    func removeToken() {
        token.delete()
        hasToken = false
        tokenError = nil
    }

    // MARK: Download and verify

    /// Called by `UpdateChecker` when it finds a release newer than this one.
    func releaseFound(_ release: ReleaseCheck.Release) {
        guard automatic, locationProblem == nil else { return }
        if case .ready(let version) = phase, version == release.version.numberString { return }
        if phase.isBusy { return }
        download(release)
    }

    func download(_ release: ReleaseCheck.Release) {
        guard !phase.isBusy else { return }
        if let locationProblem {
            phase = .failed(locationProblem.message)
            return
        }
        guard let assets = ReleaseCheck.updateAssets(in: release.assets, version: release.version) else {
            phase = .failed("Mosaic \(release.version.numberString) has no signed update file. Download it from the releases page instead.")
            return
        }
        let version = release.version.numberString
        phase = .downloading(version: version, fraction: 0)
        downloadTask = Task { [weak self] in
            guard let self else { return }
            do {
                let app = try await self.fetchAndVerify(release: release, zip: assets.zip, signature: assets.signature)
                self.stagedApp = app
                self.phase = .ready(version: version)
            } catch is CancellationError {
                self.phase = .idle
            } catch {
                self.phase = .failed(error.localizedDescription)
            }
            self.downloadTask = nil
        }
    }

    func cancel() {
        downloadTask?.cancel()
    }

    private func fetchAndVerify(release: ReleaseCheck.Release, zip: ReleaseCheck.Asset,
                                signature: ReleaseCheck.Asset) async throws -> URL {
        let version = release.version.numberString
        let folder = Self.stagingRoot.appendingPathComponent(version, isDirectory: true)
        let fileManager = FileManager.default
        try? fileManager.removeItem(at: folder)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)

        let signatureFile = folder.appendingPathComponent(signature.name)
        try await UpdateDownload(request: request(for: signature), destination: signatureFile).run()
        let signatureText = try String(contentsOf: signatureFile, encoding: .utf8)

        let zipFile = folder.appendingPathComponent(zip.name)
        try await UpdateDownload(request: request(for: zip), destination: zipFile) { [weak self] fraction in
            Task { @MainActor in
                guard let self, case .downloading = self.phase else { return }
                self.phase = .downloading(version: version, fraction: fraction)
            }
        }.run()
        try Task.checkCancellation()

        phase = .verifying(version: version)
        let signed = await Task.detached {
            guard let data = try? Data(contentsOf: zipFile, options: .mappedIfSafe) else { return false }
            return UpdatePackage.verify(data, signature: signatureText)
        }.value
        guard signed else {
            try? fileManager.removeItem(at: folder)
            throw UpdateError("The download is not signed with Mosaic’s release key, so it was discarded.")
        }

        let unpacked = folder.appendingPathComponent("unpacked", isDirectory: true)
        try await Self.run("/usr/bin/ditto", ["-x", "-k", zipFile.path, unpacked.path], failure: "The update could not be unpacked.")
        try? fileManager.removeItem(at: zipFile)
        let app = unpacked.appendingPathComponent("Mosaic.app", isDirectory: true)
        try await Self.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path],
                           failure: "The downloaded app is damaged (its code signature does not verify).")
        guard let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")),
              info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              let builtVersion = (info["CFBundleShortVersionString"] as? String).flatMap(SoftwareVersion.init),
              builtVersion == release.version
        else { throw UpdateError("The downloaded app is not Mosaic \(version).") }
        if let current = SoftwareVersion(UpdateChecker.currentVersion), builtVersion <= current {
            throw UpdateError("The download is not newer than the Mosaic you have.")
        }
        return app
    }

    private func request(for asset: ReleaseCheck.Asset) throws -> URLRequest {
        var request: URLRequest
        if hasToken, let api = asset.apiURL {
            request = URLRequest(url: api)
            request.setValue("application/octet-stream", forHTTPHeaderField: "Accept")
            authorize(&request)
        } else if let browser = asset.browserURL {
            request = URLRequest(url: browser)
        } else {
            throw UpdateError("The release has no download link for \(asset.name).")
        }
        request.timeoutInterval = 60
        return request
    }

    private static func run(_ tool: String, _ arguments: [String], failure: String) async throws {
        let status = await Task.detached { () -> Int32 in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: tool)
            process.arguments = arguments
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            do { try process.run() } catch { return -1 }
            process.waitUntilExit()
            return process.terminationStatus
        }.value
        guard status == 0 else { throw UpdateError(failure) }
    }

    // MARK: Install

    /// Quits Mosaic, installs the downloaded update and opens the new version.
    func restartToUpdate() {
        guard case .ready = phase else { return }
        relaunchAfterInstall = true
        NSApp.terminate(nil)
    }

    /// Called while Mosaic quits: hands a downloaded update to the installer script, which
    /// finishes once this process has exited.
    func installIfReady() {
        guard case .ready = phase, let stagedApp, !installerLaunched,
              FileManager.default.fileExists(atPath: stagedApp.path)
        else { return }
        let script = Self.stagingRoot.appendingPathComponent("install.sh")
        let log = Self.stagingRoot.appendingPathComponent("install.log")
        do {
            try UpdatePackage.installerScript.write(to: script, atomically: true, encoding: .utf8)
            FileManager.default.createFile(atPath: log.path, contents: nil)
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/bin/bash")
            // A test profile keeps its environment (MOSAIC_DATA_DIR and so on) by starting the
            // executable directly; a normal install goes through Launch Services.
            let direct = AppPaths.isIsolatedProfile
            process.arguments = [script.path, String(getpid()), Bundle.main.bundleURL.path, stagedApp.path,
                                 relaunchAfterInstall ? "relaunch" : "stay", direct ? "direct" : "open"]
                + (direct ? Array(CommandLine.arguments.dropFirst()) : [])
            let handle = try FileHandle(forWritingTo: log)
            process.standardOutput = handle
            process.standardError = handle
            try process.run()
            installerLaunched = true
        } catch {
            NSLog("Mosaic: could not start the update installer: %@", error.localizedDescription)
        }
    }

    // MARK: Automation

    var automationState: [String: Any] {
        var phaseName = "idle", detail = ""
        switch phase {
        case .idle: break
        case .downloading(let version, let fraction):
            phaseName = "downloading"; detail = "\(version) \(fraction.map { String(format: "%.2f", $0) } ?? "?")"
        case .verifying(let version): phaseName = "verifying"; detail = version
        case .ready(let version): phaseName = "ready"; detail = version
        case .failed(let message): phaseName = "failed"; detail = message
        }
        return [
            "phase": phaseName, "detail": detail, "automatic": automatic, "hasToken": hasToken,
            "locationProblem": locationProblem?.message ?? "", "appPath": Bundle.main.bundleURL.path,
            "version": UpdateChecker.currentVersion,
        ]
    }
}

struct UpdateError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

// MARK: - Download with progress

/// One file download: progress callbacks, cancellation, and no Authorization header after
/// a redirect to another host (GitHub hands asset downloads to a storage server that
/// rejects it).
private final class UpdateDownload: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let request: URLRequest
    private let destination: URL
    private let progress: (@Sendable (Double?) -> Void)?
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Error>?
    private var task: URLSessionDownloadTask?
    private var moveError: Error?

    init(request: URLRequest, destination: URL, progress: (@Sendable (Double?) -> Void)? = nil) {
        self.request = request
        self.destination = destination
        self.progress = progress
    }

    func run() async throws {
        let session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                lock.lock()
                self.continuation = continuation
                let task = session.downloadTask(with: request)
                self.task = task
                lock.unlock()
                task.resume()
            }
        } onCancel: {
            lock.lock()
            let task = self.task
            lock.unlock()
            task?.cancel()
        }
    }

    private func finish(_ error: Error?) {
        lock.lock()
        let continuation = self.continuation
        self.continuation = nil
        lock.unlock()
        if let error { continuation?.resume(throwing: error) } else { continuation?.resume() }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        progress?(totalBytesExpectedToWrite > 0 ? Double(totalBytesWritten) / Double(totalBytesExpectedToWrite) : nil)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            moveError = UpdateError(status == 404
                ? "GitHub did not find the update file (HTTP 404). If Mosaic’s repository is private, add a GitHub token below."
                : "The update download failed (HTTP \(status)).")
            return
        }
        do {
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: location, to: destination)
        } catch {
            moveError = error
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error = error as? URLError, error.code == .cancelled {
            finish(CancellationError())
        } else {
            finish(error ?? moveError)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        var next = request
        if next.url?.host != task.originalRequest?.url?.host {
            next.setValue(nil, forHTTPHeaderField: "Authorization")
        }
        // Never follow a redirect from HTTPS down to plain HTTP.
        if task.originalRequest?.url?.scheme == "https", next.url?.scheme != "https" {
            completionHandler(nil)
            return
        }
        completionHandler(next)
    }
}

// MARK: - GitHub token

/// A GitHub personal access token in the login Keychain, used only for requests to
/// api.github.com while Mosaic's repository is private. A test profile has its own item.
private struct GitHubTokenStore {
    let service: String = {
        guard AppPaths.isIsolatedProfile else { return "dev.mosaic.browser.github-token" }
        let digest = SHA256.hash(data: Data(AppPaths.support.standardizedFileURL.path.utf8))
        return "dev.mosaic.browser.github-token.test." + digest.prefix(6).map { String(format: "%02x", $0) }.joined()
    }()
    let account = ReleaseCheck.repository

    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    func read() -> String? {
        var query = query
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data, let token = String(data: data, encoding: .utf8), !token.isEmpty
        else { return nil }
        return token
    }

    /// Returns an error message, or nil on success.
    func write(_ token: String) -> String? {
        delete()
        var attributes = query
        attributes[kSecValueData as String] = Data(token.utf8)
        attributes[kSecAttrLabel as String] = "GitHub token for Mosaic updates"
        attributes[kSecAttrComment as String] = "Used by Mosaic to check for and download its own releases."
        let status = SecItemAdd(attributes as CFDictionary, nil)
        return status == errSecSuccess ? nil : "The token could not be saved in the Keychain (error \(status))."
    }

    func delete() {
        SecItemDelete(query as CFDictionary)
    }
}
