import AppKit
import CryptoKit
import MosaicCore

/// Where Mosaic keeps its data.
enum AppPaths {
    /// The user's real Mosaic folder in Application Support.
    static let standardSupport: URL = FileManager.default
        .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Mosaic", isDirectory: true)

    /// Where this instance keeps its data. `MOSAIC_DATA_DIR` points it at another folder:
    /// an isolated profile for testing, used by the automated checks so that several copies
    /// can run at once without touching the user's real profile.
    static let support: URL = ProcessInfo.processInfo.environment["MOSAIC_DATA_DIR"]
        .flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0, isDirectory: true) }
        ?? standardSupport

    /// True when running on an isolated profile (see `support`).
    static var isIsolatedProfile: Bool { support != standardSupport }

    /// Preferences for this instance. An isolated profile keeps its own, so a test run
    /// never changes the user's settings. Always use this, never `UserDefaults.standard`.
    static let defaults: UserDefaults = {
        guard isIsolatedProfile else { return .standard }
        // Named after the folder (a stable digest; `hashValue` changes between launches).
        let digest = SHA256.hash(data: Data(support.standardizedFileURL.path.utf8))
        let suite = "dev.mosaic.browser.isolated." + digest.prefix(8).map { String(format: "%02x", $0) }.joined()
        return UserDefaults(suiteName: suite) ?? .standard
    }()

    static var geckoProfile: URL { support.appendingPathComponent("Gecko/Profile", isDirectory: true) }
    static var chromiumData: URL { support.appendingPathComponent("Chromium", isDirectory: true) }
    static var extensions: URL { support.appendingPathComponent("Extensions", isDirectory: true) }
    /// Filter lists of the built-in content blocker.
    static var blockerLists: URL { support.appendingPathComponent("Blocker/lists", isDirectory: true) }

    /// Folder inside the app bundle holding `agent.sys.mjs`.
    static var geckoAgent: URL? { Bundle.main.resourceURL?.appendingPathComponent("gecko", isDirectory: true) }
}

/// Mosaic's Mozilla engine.
///
/// Gecko has no embedding API on macOS, so it cannot live inside a view the way
/// Chromium does. Instead Mosaic runs a private Gecko runtime as a companion
/// process with all of its own UI removed, and keeps that chromeless window
/// docked exactly over the content area of Mosaic's window ("the dock rect").
/// Control goes through `agent.sys.mjs`, loaded into Gecko over a private
/// debugging socket.
@MainActor
final class GeckoEngine: ObservableObject {
    enum Status: Equatable {
        case stopped
        case starting
        case running
        case failed(String)
    }

    enum GeckoError: LocalizedError {
        case noRuntime
        case notRunning
        case startTimedOut(String)
        case exitedDuringStartup
        case agent(String)

        var errorDescription: String? {
            switch self {
            case .noRuntime:
                "No Gecko runtime found. Run Scripts/bootstrap.sh, or install Firefox."
            case .notRunning: "Gecko is not running."
            case .startTimedOut(let stage): "Gecko did not finish starting (\(stage))."
            case .exitedDuringStartup: "Gecko quit while it was starting."
            case .agent(let message): message
            }
        }
    }

    struct TabState: Decodable, Equatable {
        let id: Int
        let url: String
        let title: String
        let loading: Bool
        let canGoBack: Bool
        let canGoForward: Bool
        let selected: Bool
        /// Requests the content blocker stopped since the page started loading.
        let blocked: Int
    }

    struct Event: Decodable, Equatable {
        let type: String
    }

    struct Snapshot: Decodable {
        let ready: Bool
        let tabs: [TabState]
        let events: [Event]
    }

    @Published private(set) var status: Status = .stopped
    private(set) var runtime: GeckoRuntime?

    /// Delivered a few times per second while Gecko runs.
    var onSnapshot: ((Snapshot) -> Void)?
    /// Gecko went away (quit, crashed, or was stopped).
    var onStopped: (() -> Void)?
    /// Gecko finished starting; the place to re-install extensions.
    var onStarted: (() async -> Void)?

    private var client: RDPClient?
    private var app: NSRunningApplication?
    private var startTask: Task<Void, Error>?
    private var pollTask: Task<Void, Never>?
    private var generation = 0

    /// Where Mosaic's content area is on screen; Gecko's window is kept there.
    private var contentRect: GeckoRect?
    /// Whether a Gecko page is what the content area currently shows.
    private var isShown = false
    private var sentRect: GeckoRect?
    private var rectInFlight = false

    var isRunning: Bool { status == .running }
    var processIdentifier: pid_t? { app?.processIdentifier }

    init() {
        runtime = Self.locateRuntime()
    }

    /// An isolated profile has no engine of its own installed, so the user's private copy
    /// (in the standard Mosaic folder) is used as well, read-only.
    private static func locateRuntime() -> GeckoRuntime? {
        // An isolated profile prefers its own private runtime, then the user's private Firefox
        // ESR, and only then a Gecko browser found in /Applications. Checking /Applications
        // first would run whichever browser happens to be installed (Zen, Nightly…), whose newer
        // Gecko marks the test profile as too new for ESR.
        if AppPaths.isIsolatedProfile, ProcessInfo.processInfo.environment["MOSAIC_GECKO_PRIVATE_ONLY"] != "1" {
            for support in [AppPaths.support, AppPaths.standardSupport] {
                let managed = GeckoRuntime.managedAppURL(applicationSupport: support)
                if let runtime = GeckoRuntime(appURL: managed, isManaged: support == AppPaths.support) {
                    return runtime
                }
            }
        }
        return GeckoRuntime.locate(applicationSupport: AppPaths.support)
    }

    // MARK: - Lifecycle

    /// Starts Gecko if needed. Concurrent callers share one launch.
    func start() async throws {
        if status == .running { return }
        if let startTask {
            try await startTask.value
            return
        }
        let task = Task { try await self.launch() }
        startTask = task
        defer { startTask = nil }
        do {
            try await task.value
        } catch {
            // Never leave a half-started Gecko running in the background.
            stop()
            status = .failed(error.localizedDescription)
            throw error
        }
    }

    private func launch() async throws {
        runtime = Self.locateRuntime()
        guard let runtime, let agentDirectory = AppPaths.geckoAgent else { throw GeckoError.noRuntime }
        status = .starting
        generation += 1
        let launchGeneration = generation

        await Self.terminateStaleEngine()
        try GeckoProfile.prepare(at: AppPaths.geckoProfile, initialRect: contentRect)

        Self.removeStaleSockets()
        let socketPath = Self.socketPath(for: getpid())
        unlink(socketPath)

        let configuration = NSWorkspace.OpenConfiguration()
        configuration.arguments = [
            "--profile", AppPaths.geckoProfile.path,
            "--no-remote",
            "--start-debugger-server", socketPath,
        ]
        // Not activated, so its window opens behind Mosaic's. It must not be
        // launched hidden: a hidden Gecko window never paints, and Gecko's
        // startup only completes after the first paint.
        configuration.activates = false
        configuration.createsNewApplicationInstance = true
        configuration.addsToRecentItems = false
        let launched = try await NSWorkspace.shared.openApplication(
            at: runtime.appURL, configuration: configuration)
        // Mosaic may have been told to stop (or quit) while Launch Services was
        // still starting the process.
        guard launchGeneration == generation else {
            launched.terminate()
            throw CancellationError()
        }
        app = launched

        // 1. The debugging socket appears once Gecko's startup has got far enough.
        let connection = try await Self.retry(seconds: 45, stage: "debugging socket") { () -> RDPClient? in
            if launched.isTerminated { throw GeckoError.exitedDuringStartup }
            return try? RDPClient(unixSocketPath: socketPath)
        }
        client = connection
        connection.onClose = { [weak self] in
            Task { @MainActor in self?.connectionClosed(generation: launchGeneration) }
        }

        // 2. Load the agent into the parent process.
        try await connection.attachToParentProcess()
        _ = try await connection.evaluate(Self.bootstrapScript(agentDirectory: agentDirectory.path))

        // 3. Wait for the browser window, then strip its chrome.
        _ = try await Self.retry(seconds: 45, stage: "browser window") { () -> Bool? in
            if launched.isTerminated { throw GeckoError.exitedDuringStartup }
            let result = try await connection.evaluate(Self.agentCall("setup", []))
            return (try Self.unwrapAgentResult(result)) == "true" ? true : nil
        }

        status = .running
        sentRect = nil
        applyDock()
        startPolling(generation: launchGeneration)
        await onStarted?()
    }

    /// Polls `attempt` until it returns a value.
    private static func retry<T>(
        seconds: Double, stage: String, _ attempt: () async throws -> T?
    ) async throws -> T {
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if let value = try await attempt() { return value }
            try await Task.sleep(nanoseconds: 150_000_000)
        }
        throw GeckoError.startTimedOut(stage)
    }

    /// Stops Gecko. Safe to call at any time, including during app termination.
    func stop() {
        let running = app
        teardown()
        status = .stopped
        guard let running, !running.isTerminated else { return }
        running.terminate()
        let pid = running.processIdentifier
        DispatchQueue.global().asyncAfter(deadline: .now() + 3) {
            if !running.isTerminated { kill(pid, SIGKILL) }
        }
    }

    /// Blocks briefly so Gecko is gone before Mosaic's process exits.
    func stopAndWait(timeout: TimeInterval = 3) {
        let running = app
        stop()
        defer {
            // Catches an engine whose launch was still in flight, which `app`
            // does not know about yet.
            for pid in Self.engineProcessIDs() { kill(pid, SIGTERM) }
        }
        guard let running else { return }
        let deadline = Date().addingTimeInterval(timeout)
        while !running.isTerminated, Date() < deadline { usleep(50_000) }
        if !running.isTerminated { kill(running.processIdentifier, SIGKILL) }
        unlink(Self.socketPath(for: getpid()))
    }

    private func teardown() {
        generation += 1
        pollTask?.cancel()
        pollTask = nil
        client?.onClose = nil
        client?.close()
        client = nil
        app = nil
        sentRect = nil
        rectInFlight = false
    }

    private func connectionClosed(generation closed: Int) {
        guard closed == generation, status == .running || status == .starting else { return }
        let wasRunning = status == .running
        let running = app
        teardown()
        status = .stopped
        // The socket only closes when Gecko is going away; make sure it does.
        if let running, !running.isTerminated { running.terminate() }
        if wasRunning { onStopped?() }
    }

    /// A previous Mosaic that crashed can leave its Gecko behind, holding the
    /// profile lock; a second Gecko on the same profile would just hand its
    /// command line to the first and exit.
    private static func terminateStaleEngine() async {
        let stale = engineProcessIDs()
        guard !stale.isEmpty else { return }
        for pid in stale { kill(pid, SIGTERM) }
        for _ in 0..<30 where stale.contains(where: { kill($0, 0) == 0 }) {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        for pid in stale where kill(pid, 0) == 0 { kill(pid, SIGKILL) }
        try? await Task.sleep(nanoseconds: 200_000_000)
    }

    /// Every process running on Mosaic's private Gecko profile. That profile is
    /// used by nothing else, so this never matches the user's own Firefox.
    private static func engineProcessIDs() -> [pid_t] {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/ps")
        process.arguments = ["-axo", "pid=,command="]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return [] }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let marker = "--profile \(AppPaths.geckoProfile.path) "
        return (String(data: data, encoding: .utf8) ?? "").split(separator: "\n").compactMap { line in
            // Only the main process carries "--profile <path>" as two arguments;
            // its children are terminated by it.
            guard line.contains(marker), line.contains("--start-debugger-server") else { return nil }
            return pid_t(line.trimmingCharacters(in: .whitespaces).prefix { $0.isNumber })
        }
    }

    /// The private debugging socket lives in the per-user temporary directory
    /// (mode 0700), named after the Mosaic process that owns it.
    private static func socketPath(for pid: pid_t) -> String {
        (NSTemporaryDirectory() as NSString).appendingPathComponent("mosaic-gecko-\(pid).sock")
    }

    /// Sockets left behind by Mosaic processes that no longer exist.
    private static func removeStaleSockets() {
        let directory = NSTemporaryDirectory()
        let names = (try? FileManager.default.contentsOfDirectory(atPath: directory)) ?? []
        for name in names where name.hasPrefix("mosaic-gecko-") && name.hasSuffix(".sock") {
            let digits = name.dropFirst("mosaic-gecko-".count).dropLast(".sock".count)
            guard let pid = pid_t(digits), pid != getpid(), kill(pid, 0) != 0 else { continue }
            unlink((directory as NSString).appendingPathComponent(name))
        }
    }

    // MARK: - Agent calls

    private static let agentExpression =
        #"ChromeUtils.importESModule("resource://mosaic/agent.sys.mjs").Agent"#

    /// Registers `resource://mosaic/` → the agent folder and loads the agent.
    private static func bootstrapScript(agentDirectory: String) -> String {
        """
        (() => {
          const Ci = Components.interfaces, Cc = Components.classes;
          const resource = Services.io.getProtocolHandler("resource")
            .QueryInterface(Ci.nsISubstitutingProtocolHandler);
          const directory = Cc["@mozilla.org/file/local;1"].createInstance(Ci.nsIFile);
          directory.initWithPath(\(jsLiteral(agentDirectory)));
          resource.setSubstitution("mosaic", Services.io.newFileURI(directory));
          return \(agentExpression).version();
        })()
        """
    }

    /// A call to an agent method, wrapped so that whatever happens comes back
    /// as one JSON string: `{"ok":true,"value":<the method's JSON>}` or
    /// `{"ok":false,"error":"<message>"}`. Without the wrapper a failing async
    /// method would be indistinguishable from one that returned nothing, since
    /// the debugging protocol reports a rejected promise without its reason.
    private static func agentCall(_ method: String, _ arguments: [Any]) -> String {
        let call = "\(agentExpression).\(method)(\(arguments.map(jsLiteral).joined(separator: ", ")))"
        return """
        (async () => {
          try {
            return JSON.stringify({ ok: true, value: await \(call) });
          } catch (error) {
            return JSON.stringify({ ok: false, error: String(error?.message ?? error) });
          }
        })()
        """
    }

    /// Unwraps `agentCall`'s envelope into the method's own JSON string.
    private static func unwrapAgentResult(_ result: Any?) throws -> String? {
        guard let text = result as? String, let data = text.data(using: .utf8),
              let envelope = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        else { return nil }
        guard (envelope["ok"] as? Bool) == true else {
            throw GeckoError.agent((envelope["error"] as? String) ?? "the Gecko agent reported an error")
        }
        return envelope["value"] as? String
    }

    /// JSON is a subset of JavaScript, so a JSON-encoded value is a safe literal.
    private static func jsLiteral(_ value: Any) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: [value]),
              let text = String(data: data, encoding: .utf8)
        else { return "null" }
        return String(text.dropFirst().dropLast())
    }

    /// Calls a method of the agent and returns its decoded JSON result.
    @discardableResult
    func call(_ method: String, _ arguments: Any...) async throws -> Any? {
        guard status == .running, let client else { throw GeckoError.notRunning }
        let result = try await client.evaluate(Self.agentCall(method, arguments))
        guard let text = try Self.unwrapAgentResult(result), let data = text.data(using: .utf8) else { return nil }
        let value = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        return value is NSNull ? nil : value
    }

    /// Fire-and-forget variant for UI actions.
    func send(_ method: String, _ arguments: Any...) {
        guard status == .running, let client else { return }
        let script = Self.agentCall(method, arguments)
        Task { _ = try? await client.evaluate(script) }
    }

    private func startPolling(generation polled: Int) {
        pollTask?.cancel()
        pollTask = Task { [weak self] in
            let decoder = JSONDecoder()
            while !Task.isCancelled {
                guard let self, self.generation == polled, let client = self.client else { return }
                if let result = try? await client.evaluate(Self.agentCall("state", [])),
                   let text = (try? Self.unwrapAgentResult(result)) ?? nil,
                   let snapshot = try? decoder.decode(Snapshot.self, from: Data(text.utf8)),
                   self.generation == polled, snapshot.ready
                {
                    self.onSnapshot?(snapshot)
                }
                try? await Task.sleep(nanoseconds: 200_000_000)
            }
        }
    }

    // MARK: - Docking

    /// Keeps Gecko's window over `rect` (Mosaic's content area), and shows it
    /// only while `shown`: when the content area displays something else, the
    /// whole Gecko app is hidden so its window can never cover Mosaic's.
    func dock(_ rect: GeckoRect?, shown: Bool) {
        let visible = shown && rect != nil
        guard rect != contentRect || visible != isShown else { return }
        if let rect { contentRect = rect }
        isShown = visible
        applyDock()
    }

    private func applyDock() {
        guard status == .running, let app else { return }
        pushRect()
        if isShown {
            if app.isHidden { app.unhide() }
        } else if !app.isHidden {
            app.hide()
        }
    }

    /// Sends the newest rect; while one is in flight, later ones coalesce.
    private func pushRect() {
        guard !rectInFlight, status == .running, let rect = contentRect, rect != sentRect else { return }
        rectInFlight = true
        let current = generation
        Task {
            _ = try? await call("setRect", rect.x, rect.y, rect.width, rect.height)
            guard current == generation else { return }
            sentRect = rect
            rectInFlight = false
            pushRect()
        }
    }

    // MARK: - Runtime install (owner: platform)

    /// Looks for the Gecko runtime again, after Mosaic installed, updated or removed its
    /// private copy (Settings › Engine). The next start uses whatever is found.
    func refreshRuntime() {
        runtime = Self.locateRuntime()
    }

    /// Makes Gecko the active app so the page receives keyboard input.
    func activate() {
        guard status == .running, isShown else { return }
        app?.activate()
    }

    /// The window-server number of Gecko's browser window, if it is on screen.
    func windowNumber() -> Int? {
        guard let pid = app?.processIdentifier else { return nil }
        return Self.onScreenWindows().first { $0.pid == pid && $0.area > 10_000 }?.number
    }

    struct WindowInfo {
        let number: Int
        let pid: pid_t
        let area: CGFloat
    }

    /// Normal-level windows, front to back.
    static func onScreenWindows() -> [WindowInfo] {
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { return [] }
        return list.compactMap { entry in
            guard (entry[kCGWindowLayer as String] as? Int) == 0,
                  let number = entry[kCGWindowNumber as String] as? Int,
                  let pid = entry[kCGWindowOwnerPID as String] as? pid_t,
                  let bounds = entry[kCGWindowBounds as String] as? [String: CGFloat]
            else { return nil }
            return WindowInfo(number: number, pid: pid, area: (bounds["Width"] ?? 0) * (bounds["Height"] ?? 0))
        }
    }
}
