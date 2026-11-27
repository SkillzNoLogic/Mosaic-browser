import AppKit
import CEFBridge
import Combine
import MosaicCore

/// Downloads from both engines behind one list: progress, pause/resume/cancel/retry,
/// opening and revealing files, the persistent history, safety checks, notifications and
/// the hand-off to Free Download Manager. Owner: **downloads**.
///
/// Chromium reports through `MosaicCEFDownloads` (CEFDownloads.mm); Gecko through the
/// agent's downloads methods, polled by `DownloadManager+Gecko.swift`.
@MainActor
final class DownloadManager: ObservableObject {
    /// Every download Mosaic knows about, newest first (the history).
    @Published private(set) var items: [DownloadRecord] = []
    /// The toolbar button's popover.
    @Published var isPopoverShown = false {
        didSet { if isPopoverShown { hasUnseenCompletion = false } }
    }
    /// A download finished since the popover was last opened.
    @Published private(set) var hasUnseenCompletion = false
    /// The Downloads page's search field (kept here so it survives switching tabs).
    @Published var searchText = ""

    let settings = DownloadSettings()
    let fdm = FreeDownloadManager()
    let prompter = DownloadPrompter()
    let notifier: DownloadNotifier
    /// Launched with `--mosaic-automation`: never open files, Finder or other apps on the
    /// screen, never ask for notification permission (see `DownloadNotifier`).
    let isScripted: Bool

    weak var model: BrowserModel?
    let gecko: GeckoEngine
    let store: DownloadHistoryStore

    // Links between records and the engines' own download IDs.
    var chromiumLinks: [UInt32: UUID] = [:]
    var geckoLinks: [Int: UUID] = [:]
    /// Gecko downloads Mosaic stopped or dropped (handed off, duplicate, save cancelled).
    var ignoredGeckoIDs: Set<Int> = []
    /// URLs Mosaic is starting in Gecko itself; their downloads are linked by `startInGecko`.
    var pendingGeckoStarts: [String: Int] = [:]
    /// Downloads still waiting for an answer (Free Download Manager, duplicate, save as).
    var undecided: Set<UUID> = []
    /// Gecko saves into the download folder; files that belong elsewhere are moved here
    /// when they complete.
    var geckoDestinations: [UUID: URL] = [:]
    /// Completed downloads whose file has been finished off (moved, quarantined, announced).
    var finishedIDs: Set<UUID> = []
    /// Chromium retries in flight: URL → the record to reuse.
    var pendingRetries: [String: UUID] = [:]
    /// Paths promised to downloads still running, so two downloads never get the same name.
    var reservedPaths: Set<String> = []
    /// For "Download in Mosaic instead" after FDM: the engine a handed-off item came from.
    var fdmSourceEngines: [UUID: EngineKind] = [:]

    var geckoVersion = -1
    var geckoPollTask: Task<Void, Never>?
    var geckoWatching = false

    // Diagnostics for `dump`.
    var lastOpenRequest: String?
    var lastAutoOpen: String?
    var lastFDMRoute: String?

    private let chromiumBridge = ChromiumDownloadsBridge()
    private var saveTask: Task<Void, Never>?
    private var cancellables: Set<AnyCancellable> = []

    init(model: BrowserModel) {
        self.model = model
        gecko = model.gecko
        isScripted = CommandLine.arguments.contains(Automation.flag)
        notifier = DownloadNotifier(isScripted: isScripted)
        store = DownloadHistoryStore(fileURL: AppPaths.support.appendingPathComponent("Downloads/history.json"))
        store.load()
        items = store.records

        chromiumBridge.manager = self
        MosaicCEFDownloads.delegate = chromiumBridge
        settings.onEngineSettingsChange = { [weak self] in self?.engineSettingsChanged() }
        notifier.onOpen = { [weak self] id in self?.showFromNotification(id) }
        applyChromiumSettings()

        gecko.$status
            .sink { [weak self] status in
                // @Published reports before the value is stored; act on the next turn.
                Task { @MainActor in self?.geckoStatusChanged(status) }
            }
            .store(in: &cancellables)
        fdm.objectWillChange
            .sink { [weak self] in
                Task { @MainActor in self?.applyChromiumSettings() }
            }
            .store(in: &cancellables)
    }

    // MARK: - Reading

    func record(_ id: UUID) -> DownloadRecord? { items.first { $0.id == id } }

    /// Downloads currently transferring.
    var activeCount: Int { items.lazy.filter { $0.state == .inProgress }.count }

    /// Combined progress of the running downloads, or nil when a size is unknown.
    var overallProgress: Double? {
        let running = items.filter { $0.state == .inProgress }
        guard !running.isEmpty, running.allSatisfy({ $0.totalBytes > 0 }) else { return nil }
        let total = running.reduce(Int64(0)) { $0 + $1.totalBytes }
        let received = running.reduce(Int64(0)) { $0 + $1.receivedBytes }
        return total > 0 ? min(1, Double(received) / Double(total)) : nil
    }

    /// Items matching the page's search.
    var filteredItems: [DownloadRecord] { items.filter { $0.matches(searchText) } }

    func fileURL(of record: DownloadRecord) -> URL? {
        record.destinationPath.map { URL(fileURLWithPath: $0) }
    }

    func fileExists(_ record: DownloadRecord) -> Bool {
        guard let path = record.destinationPath else { return false }
        return FileManager.default.fileExists(atPath: path)
    }

    // MARK: - Writing

    func insert(_ record: DownloadRecord) {
        store.upsert(record)
        items = store.records
        saveSoon(immediately: true)
    }

    /// Changes a record; state changes are saved at once, progress every few seconds.
    func update(_ id: UUID, _ change: (inout DownloadRecord) -> Void) {
        guard var record = store.record(id) else { return }
        let before = record
        change(&record)
        guard record != before else { return }
        store.upsert(record)
        items = store.records
        saveSoon(immediately: record.state != before.state || record.destinationPath != before.destinationPath)
    }

    func deleteRecord(_ id: UUID) {
        store.remove(id)
        items = store.records
        saveSoon(immediately: true)
    }

    private func saveSoon(immediately: Bool) {
        if immediately {
            saveTask?.cancel()
            saveTask = nil
            try? store.save()
            return
        }
        guard saveTask == nil else { return }
        saveTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            guard !Task.isCancelled else { return }
            self.saveTask = nil
            try? self.store.save()
        }
    }

    // MARK: - Settings

    private func engineSettingsChanged() {
        applyChromiumSettings()
        if gecko.isRunning { Task { await configureGecko() } }
    }

    private func applyChromiumSettings() {
        MosaicCEFDownloads.showsFDMContextMenuItem = fdm.isInstalled && settings.fdmContextMenu
    }

    /// Creates the download folder if needed; falls back to ~/Downloads if it cannot.
    func ensureFolder() -> URL {
        let folder = settings.folder
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            return folder
        } catch {
            return DownloadSettings.defaultFolder
        }
    }

    // MARK: - Deciding how a download starts

    enum StartChoice {
        /// Download it; to this file, or to the default place when nil.
        case save(URL?)
        case cancel
        case handOff
        case openExisting(DownloadRecord)
    }

    /// The questions asked before a download goes ahead: Free Download Manager (per the
    /// settings), duplicate download, where to save. Retries ask nothing.
    func chooseStart(for id: UUID, isRetry: Bool) async -> StartChoice {
        notifier.requestAuthorizationIfNeeded()
        guard let record = record(id) else { return .cancel }
        if isRetry { return .save(nil) }

        fdm.refresh()
        let size = record.totalBytes >= 0 ? record.totalBytes : nil
        switch settings.fdmPolicy.decide(url: record.url, totalBytes: size, fdmInstalled: fdm.isInstalled) {
        case .sendToFDM:
            return .handOff
        case .askUser:
            let sizeText = size.map { " (\(DownloadFormat.bytes($0)))" } ?? ""
            let answer = await prompter.ask(
                kind: "fdm", title: "Download “\(record.fileName)” with Free Download Manager?",
                message: "From \(record.host ?? record.url)\(sizeText). Free Download Manager will ask you to confirm in its own window.",
                buttons: ["Use Free Download Manager", "Download in Mosaic"])
            if answer == 0 { return .handOff }
        case .useEngine:
            break
        }

        if let existing = store.completedDownload(of: record.url, excluding: id) {
            let folder = existing.destinationPath.map {
                (($0 as NSString).deletingLastPathComponent as NSString).abbreviatingWithTildeInPath
            } ?? "your download folder"
            let when = existing.endedAt.map {
                " on " + $0.formatted(date: .abbreviated, time: .shortened)
            } ?? ""
            let answer = await prompter.ask(
                kind: "duplicate", title: "You already downloaded “\(existing.fileName)”",
                message: "The file from this address is in \(folder) (downloaded\(when)). Download it again?",
                buttons: ["Open Existing File", "Download Again", "Cancel"])
            switch answer {
            case 0: return .openExisting(existing)
            case 1, nil: break
            default: return .cancel
            }
        }

        if settings.askWhereToSave {
            let directory = settings.rules.directory(
                forFileName: record.fileName, mimeType: record.mimeType, in: ensureFolder())
            guard let chosen = await prompter.chooseDestination(suggestedName: record.fileName, directory: directory)
            else { return .cancel }
            return .save(chosen)
        }
        return .save(nil)
    }

    /// Where a file goes by default: the download folder, or its type's subfolder, under a
    /// name not used by an existing file or another running download.
    func defaultDestination(fileName: String, mimeType: String?) -> URL {
        let directory = settings.rules.directory(forFileName: fileName, mimeType: mimeType, in: ensureFolder())
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return uniqueURL(in: directory, name: fileName)
    }

    func uniqueURL(in directory: URL, name: String) -> URL {
        let free = DownloadFileName.unique(DownloadFileName.sanitize(name)) { candidate in
            let path = directory.appendingPathComponent(candidate).path
            return FileManager.default.fileExists(atPath: path) || self.reservedPaths.contains(path)
        }
        return directory.appendingPathComponent(free)
    }

    // MARK: - Finishing

    /// A download completed: put the file where it belongs, quarantine it, run the safety
    /// check, announce it, and open it if the user wants safe files opened.
    func finishCompleted(_ id: UUID) {
        guard !finishedIDs.contains(id), !undecided.contains(id), let record = record(id) else { return }
        finishedIDs.insert(id)
        if let path = record.destinationPath { reservedPaths.remove(path) }

        if record.engine == .gecko {
            relocateGeckoFile(id)
            if let geckoID = geckoLinks.first(where: { $0.value == id })?.key {
                // Mosaic keeps the history from here on; Gecko's own list stays short.
                geckoLinks.removeValue(forKey: geckoID)
                ignoredGeckoIDs.insert(geckoID)
                gecko.send("downloadAction", geckoID, "remove")
            }
        }

        guard let record = self.record(id), let url = fileURL(of: record),
              FileManager.default.fileExists(atPath: url.path) else { return }
        Quarantine.ensure(url, source: record.url, referrer: record.referrer)
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init) ?? record.totalBytes
        update(id) {
            $0.endedAt = $0.endedAt ?? Date()
            $0.speed = 0
            $0.canResume = false
            if size > 0 {
                $0.totalBytes = size
                $0.receivedBytes = size
            }
        }
        if !isPopoverShown { hasUnseenCompletion = true }

        if settings.notifyWhenFinished {
            let warning = record.needsSafetyConfirmation ? " Check the warning before opening it." : ""
            if NSApp.isActive, model?.selectedTab?.internalPage != .downloads {
                model?.notice = "Downloaded “\(record.fileName)”"
            } else if !NSApp.isActive {
                notifier.post(title: "Download finished", body: "“\(record.fileName)”.\(warning)", id: id)
            }
        }

        if settings.openSafeFiles, !record.needsSafetyConfirmation,
           DownloadSafety.isSafeToOpenAutomatically(record.fileName)
        {
            lastAutoOpen = url.path
            if !isScripted { NSWorkspace.shared.open(url) }
        }
    }

    /// Moves a completed Gecko file to the place chosen in "Save As", or to its type's
    /// subfolder. Gecko itself always saves into the download folder.
    private func relocateGeckoFile(_ id: UUID) {
        guard let record = record(id), let path = record.destinationPath else { return }
        let current = URL(fileURLWithPath: path)
        let target: URL
        if let chosen = geckoDestinations.removeValue(forKey: id) {
            target = chosen
        } else {
            let directory = settings.rules.directory(
                forFileName: current.lastPathComponent, mimeType: record.mimeType, in: settings.folder)
            guard directory.standardizedFileURL.path != current.deletingLastPathComponent().standardizedFileURL.path
            else { return }
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            target = uniqueURL(in: directory, name: current.lastPathComponent)
        }
        guard target.standardizedFileURL.path != current.standardizedFileURL.path else { return }
        do {
            try FileManager.default.createDirectory(
                at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: target.path) {
                // "Save As" already asked whether to replace it.
                try FileManager.default.removeItem(at: target)
            }
            try FileManager.default.moveItem(at: current, to: target)
            update(id) {
                $0.destinationPath = target.path
                $0.fileName = target.lastPathComponent
            }
        } catch {
            update(id) { $0.errorMessage = "Saved in \(settings.folderDisplayPath): could not move it (\(error.localizedDescription))" }
        }
    }

    // MARK: - Actions

    func pause(_ id: UUID) {
        guard let record = record(id), record.state == .inProgress else { return }
        switch record.engine {
        case .chromium:
            if let link = chromiumID(of: id) { MosaicCEFDownloads.pauseDownload(link) }
        case .gecko:
            if let link = geckoID(of: id) { gecko.send("downloadAction", link, "pause") }
        case .fdm:
            break
        }
    }

    func resume(_ id: UUID) {
        guard let record = record(id), record.state == .paused else { return }
        switch record.engine {
        case .chromium:
            if let link = chromiumID(of: id), MosaicCEFDownloads.isControllable(link) {
                MosaicCEFDownloads.resumeDownload(link)
            } else {
                retry(id)
            }
        case .gecko:
            if let link = geckoID(of: id) { gecko.send("downloadAction", link, "resume") } else { retry(id) }
        case .fdm:
            break
        }
    }

    /// Stops the download and removes its partial data; it stays listed and can be retried.
    func cancel(_ id: UUID) {
        guard let record = record(id) else { return }
        switch record.engine {
        case .chromium:
            if let link = chromiumID(of: id), MosaicCEFDownloads.isControllable(link) {
                MosaicCEFDownloads.cancelDownload(link)
            } else if record.state.isActive {
                update(id) { $0.state = .cancelled; $0.speed = 0; $0.endedAt = Date() }
            }
        case .gecko:
            if let link = geckoID(of: id) {
                gecko.send("downloadAction", link, "cancel")
            } else if record.state.isActive {
                update(id) { $0.state = .cancelled; $0.speed = 0; $0.endedAt = Date() }
            }
        case .fdm:
            break
        }
    }

    /// Starts a failed or cancelled download again (Chromium resumes an interrupted one
    /// where the server allows it).
    func retry(_ id: UUID) {
        guard let record = record(id), record.state != .inProgress else { return }
        finishedIDs.remove(id)
        switch record.engine {
        case .chromium:
            if let link = chromiumID(of: id), MosaicCEFDownloads.isControllable(link) {
                MosaicCEFDownloads.resumeDownload(link)
            } else {
                startInChromium(url: record.url, reusing: id)
            }
        case .gecko:
            if let link = geckoID(of: id) {
                update(id) { $0.state = .inProgress; $0.errorMessage = nil; $0.endedAt = nil }
                gecko.send("downloadAction", link, "retry")
            } else {
                startInGecko(url: record.url, reusing: id)
            }
        case .fdm:
            let engine = fdmSourceEngines[id] ?? model?.engine ?? .gecko
            if engine == .chromium { startInChromium(url: record.url, reusing: id) } else { startInGecko(url: record.url, reusing: id) }
        }
    }

    /// Opens the file, after a confirmation if its type or name deserves a warning.
    /// Gatekeeper still checks it, because it carries the quarantine attribute.
    func open(_ id: UUID) {
        guard let record = record(id), record.state == .completed else { return }
        guard let url = fileURL(of: record), FileManager.default.fileExists(atPath: url.path) else {
            model?.notice = "“\(record.fileName)” was moved or deleted."
            return
        }
        Task {
            if record.needsSafetyConfirmation {
                let summary = record.assessment.summary ?? ""
                let answer = await prompter.ask(
                    kind: "safety", title: "Open “\(record.fileName)”?",
                    message: "\(summary) Open it only if you trust \(record.host ?? "its source").",
                    buttons: ["Open", "Cancel"], style: .warning)
                guard answer == 0 else { return }
                update(id) { $0.warningAcknowledged = true }
            }
            lastOpenRequest = url.path
            if !isScripted { NSWorkspace.shared.open(url) }
        }
    }

    func revealInFinder(_ id: UUID) {
        guard let record = record(id), let url = fileURL(of: record) else { return }
        let target = FileManager.default.fileExists(atPath: url.path) ? url : url.deletingLastPathComponent()
        lastOpenRequest = "reveal:" + target.path
        if !isScripted { NSWorkspace.shared.activateFileViewerSelecting([target]) }
    }

    /// The user read the warning and keeps the file.
    func keep(_ id: UUID) {
        update(id) { $0.warningAcknowledged = true }
    }

    /// Moves the file to the Trash and removes it from the list.
    func trash(_ id: UUID) {
        guard let record = record(id) else { return }
        if record.state.isActive { cancel(id) }
        if let url = fileURL(of: record), FileManager.default.fileExists(atPath: url.path) {
            NSWorkspace.shared.recycle([url]) { _, _ in }
        }
        remove(id)
    }

    /// Removes the entry from the list (the file stays). A running download is cancelled.
    func remove(_ id: UUID) {
        guard let record = record(id) else { return }
        if record.state.isActive { cancel(id) }
        if let link = geckoID(of: id) {
            geckoLinks.removeValue(forKey: link)
            ignoredGeckoIDs.insert(link)
            gecko.send("downloadAction", link, "remove")
        }
        if let link = chromiumID(of: id) { chromiumLinks.removeValue(forKey: link) }
        if let path = record.destinationPath { reservedPaths.remove(path) }
        undecided.remove(id)
        deleteRecord(id)
    }

    /// Removes every finished entry; running and paused downloads stay.
    func clearFinished() {
        for record in items where !record.state.isActive {
            if let link = geckoID(of: record.id) {
                geckoLinks.removeValue(forKey: link)
                ignoredGeckoIDs.insert(link)
                gecko.send("downloadAction", link, "remove")
            }
            if let link = chromiumID(of: record.id) { chromiumLinks.removeValue(forKey: link) }
        }
        store.clearFinished()
        items = store.records
        saveSoon(immediately: true)
    }

    func copyLink(_ id: UUID) {
        guard let record = record(id) else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(record.url, forType: .string)
    }

    func openFolder() {
        let folder = ensureFolder()
        lastOpenRequest = "folder:" + folder.path
        if !isScripted { NSWorkspace.shared.open(folder) }
    }

    func openDownloadsPage() {
        isPopoverShown = false
        model?.openInternalPage(.downloads)
    }

    private func showFromNotification(_ id: UUID) {
        if NSApp.isActive == false, !isScripted { NSApp.activate(ignoringOtherApps: true) }
        openDownloadsPage()
    }

    func chromiumID(of id: UUID) -> UInt32? { chromiumLinks.first { $0.value == id }?.key }
    func geckoID(of id: UUID) -> Int? { geckoLinks.first { $0.value == id }?.key }

    // MARK: - Free Download Manager

    /// "Download with Free Download Manager" for a listed download. A running download is
    /// stopped in Mosaic first; a completed one is listed again as a new entry.
    func sendToFDM(_ id: UUID) {
        guard let record = record(id), fdm.isInstalled else { return }
        let source: EngineKind? = record.engine == .chromium ? .chromium : record.engine == .gecko ? .gecko : nil
        var target = id
        if record.state == .completed {
            let copy = DownloadRecord(
                url: record.url, originalURL: record.originalURL, referrer: record.referrer,
                fileName: record.fileName, suggestedName: record.suggestedName, engine: .fdm,
                mimeType: record.mimeType, totalBytes: record.totalBytes, state: .handedOff)
            insert(copy)
            target = copy.id
        } else {
            if record.state.isActive {
                if let link = chromiumID(of: id) {
                    chromiumLinks.removeValue(forKey: link)
                    MosaicCEFDownloads.cancelDownload(link)
                }
                if let link = geckoID(of: id) {
                    geckoLinks.removeValue(forKey: link)
                    ignoredGeckoIDs.insert(link)
                    gecko.send("downloadAction", link, "remove")
                }
                if let path = record.destinationPath { reservedPaths.remove(path) }
            }
            update(id) { $0.receivedBytes = 0; $0.destinationPath = nil }
        }
        Task { await handOff(target, from: source) }
    }

    /// A link's context-menu item, in either engine.
    func sendLinkToFDM(url: String, referrer: String?, from engine: EngineKind) {
        guard fdm.isInstalled, let parsed = URL(string: url) else { return }
        let record = DownloadRecord(
            url: url, referrer: referrer, fileName: DownloadFileName.suggested(forURL: parsed),
            engine: .fdm, state: .handedOff)
        insert(record)
        Task { await handOff(record.id, from: engine) }
    }

    /// Gives the download to FDM. The record shows what happened, including FDM's answer.
    func handOff(_ id: UUID, from engine: EngineKind?) async {
        guard let record = record(id) else { return }
        fdmSourceEngines[id] = engine
        update(id) {
            $0.engine = .fdm
            $0.state = .handedOff
            $0.speed = 0
            $0.errorMessage = "Sending to Free Download Manager…"
            $0.endedAt = Date()
        }
        let info = await requestInfo(for: record.url, engine: engine ?? model?.engine ?? .chromium,
                                     includeCookies: settings.fdmPassCookies)
        let request = FDMDownloadRequest(
            url: record.url, originalURL: record.originalURL, referrer: record.referrer,
            userAgent: info.userAgent, cookies: info.cookies)
        do {
            let route = try await fdm.send(request, bringToFront: !isScripted) { [weak self] outcome in
                self?.fdmAnswered(id, outcome)
            }
            lastFDMRoute = route.rawValue
            update(id) {
                guard $0.state == .handedOff, $0.errorMessage == "Sending to Free Download Manager…" else { return }
                $0.errorMessage = route == .integration
                    ? "Sent to Free Download Manager · confirm it there"
                    : "Opened in Free Download Manager (address only)"
            }
        } catch {
            update(id) {
                $0.state = .failed
                $0.errorMessage = error.localizedDescription
            }
        }
    }

    private func fdmAnswered(_ id: UUID, _ outcome: FDMNativeMessage.Outcome) {
        update(id) {
            switch outcome {
            case .accepted:
                $0.state = .handedOff
                $0.errorMessage = "Downloading in Free Download Manager"
            case .cancelledByUser:
                $0.state = .cancelled
                $0.errorMessage = "Cancelled in Free Download Manager"
            case .failed(let message):
                $0.state = .failed
                $0.errorMessage = "Free Download Manager: \(message)"
            }
        }
    }

    /// What FDM needs to fetch the file as the engine would.
    private func requestInfo(for url: String, engine: EngineKind, includeCookies: Bool) async
        -> (userAgent: String?, cookies: String?)
    {
        switch engine {
        case .chromium:
            guard includeCookies else { return (MosaicCEFDownloads.userAgent, nil) }
            let cookies = await withCheckedContinuation { continuation in
                MosaicCEFDownloads.cookies(forURL: url) { continuation.resume(returning: $0) }
            }
            return (MosaicCEFDownloads.userAgent, cookies.isEmpty ? nil : cookies)
        case .gecko:
            guard gecko.isRunning,
                  let info = try? await gecko.call("downloadRequestInfo", url, includeCookies) as? [String: Any]
            else { return (nil, nil) }
            let cookies = info["cookies"] as? String
            return (info["userAgent"] as? String, cookies?.isEmpty == false ? cookies : nil)
        }
    }

    // MARK: - Starting downloads

    /// Starts `url` in Chromium. With `reusing`, the existing entry is continued (retry).
    func startInChromium(url: String, reusing id: UUID? = nil) {
        if let id {
            pendingRetries[url] = id
            update(id) {
                $0.state = .inProgress
                $0.engine = .chromium
                $0.errorMessage = nil
                $0.receivedBytes = 0
                $0.endedAt = nil
            }
        }
        guard MosaicCEFDownloads.startDownload(withURL: url) else {
            // No Chromium browser is open to start it from.
            if let id {
                pendingRetries.removeValue(forKey: url)
                startInGecko(url: url, reusing: id)
            } else {
                startInGecko(url: url, reusing: nil)
            }
            return
        }
    }

    /// Starts `url` in Gecko. A new download first goes through the usual questions.
    func startInGecko(url: String, reusing id: UUID? = nil, referrer: String? = nil) {
        guard let parsed = URL(string: url) else { return }
        let recordID: UUID
        if let id {
            recordID = id
        } else {
            let record = DownloadRecord(
                url: url, referrer: referrer, fileName: DownloadFileName.suggested(forURL: parsed), engine: .gecko)
            insert(record)
            recordID = record.id
        }
        update(recordID) {
            $0.state = .inProgress
            $0.engine = .gecko
            $0.errorMessage = nil
            $0.receivedBytes = 0
            $0.endedAt = nil
        }
        undecided.insert(recordID)
        Task {
            let choice = await chooseStart(for: recordID, isRetry: id != nil)
            undecided.remove(recordID)
            guard let record = record(recordID) else { return }
            let destination: URL
            switch choice {
            case .save(let chosen):
                if let chosen {
                    destination = chosen
                } else if let previous = record.destinationPath, id != nil,
                          !FileManager.default.fileExists(atPath: previous)
                {
                    destination = URL(fileURLWithPath: previous)
                } else {
                    destination = defaultDestination(fileName: record.fileName, mimeType: record.mimeType)
                }
            case .cancel:
                deleteRecord(recordID)
                return
            case .handOff:
                await handOff(recordID, from: .gecko)
                return
            case .openExisting(let existing):
                deleteRecord(recordID)
                open(existing.id)
                return
            }
            reservedPaths.insert(destination.path)
            update(recordID) {
                $0.destinationPath = destination.path
                $0.fileName = destination.lastPathComponent
            }
            do {
                try await gecko.start()
                pendingGeckoStarts[url, default: 0] += 1
                defer {
                    pendingGeckoStarts[url, default: 1] -= 1
                    if pendingGeckoStarts[url] == 0 { pendingGeckoStarts.removeValue(forKey: url) }
                }
                guard let link = try await gecko.call("startDownload", url, destination.path) as? Int else {
                    throw GeckoEngine.GeckoError.agent("Gecko did not start the download")
                }
                geckoLinks[link] = recordID
            } catch {
                reservedPaths.remove(destination.path)
                update(recordID) {
                    $0.state = .failed
                    $0.errorMessage = error.localizedDescription
                }
            }
        }
    }
}

/// Receives Chromium's download callbacks (an Objective-C protocol, hence an NSObject).
final class ChromiumDownloadsBridge: NSObject, MCEFDownloadDelegate {
    weak var manager: DownloadManager?

    func chromiumDownloadWillStart(_ download: MCEFDownload, decide: @escaping (String?) -> Void) {
        MainActor.assumeIsolated {
            guard let manager else { return decide(nil) }
            manager.chromiumWillStart(download, decide: decide)
        }
    }

    func chromiumDownloadDidUpdate(_ download: MCEFDownload) {
        MainActor.assumeIsolated { manager?.chromiumDidUpdate(download) }
    }

    func chromiumContextMenuSendLink(toFDM linkURL: String, pageURL: String) {
        MainActor.assumeIsolated {
            manager?.sendLinkToFDM(url: linkURL, referrer: pageURL.isEmpty ? nil : pageURL, from: .chromium)
        }
    }
}
