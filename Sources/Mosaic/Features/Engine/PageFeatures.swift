import AppKit
import CEFBridge
import Combine
import MosaicCore
import UniformTypeIdentifiers

/// Per-page features that need both engines: find in page, zoom, printing and PDF export,
/// developer tools, mute and audio indicators, screenshots, permission prompts, pop-up
/// blocking, JavaScript dialogs (Chromium) and page full screen.
///
/// Owned by `BrowserModel.pageFeatures`. The UI is `FindBar` (the overlay mount point the
/// shell places over the content area) plus menu items (`installMenuItems(in:)`).
@MainActor
final class PageFeatures: NSObject, ObservableObject {
    unowned let model: BrowserModel
    let zoomStore = SiteZoomStore(fileURL: AppPaths.siteZoom)
    var permissions: SitePermissionStore { model.sitePermissions }

    // MARK: Find
    @Published private(set) var isFindBarVisible = false
    /// Changing either restarts the search (once per actual change).
    @Published var findText = "" { didSet { if findText != oldValue { findQueryChanged() } } }
    @Published var findMatchCase = false { didSet { if findMatchCase != oldValue { findQueryChanged() } } }
    @Published private(set) var findStatus: FindStatus?
    /// Incremented to ask the find field to take focus (and select its text).
    @Published private(set) var findFocusRequest = 0
    private(set) var findTabID: UUID?
    /// Chromium: whether the current text has been searched once (later searches move on).
    private var chromiumFindStarted = false
    private var chromiumNeedsFirstMatch = false
    private var findGeneration = 0

    // MARK: Prompts and notices (one list each; the UI shows those of the selected tab)
    @Published var permissionPrompts: [PermissionPrompt] = []
    @Published var popupNotices: [PopupNotice] = []
    @Published var dialogs: [PageDialog] = []
    /// The tab whose page is in full screen (JavaScript Fullscreen API), if any.
    @Published var fullScreenTabID: UUID?

    let fullScreen = PageFullScreenController()
    private var cancellables: Set<AnyCancellable> = []
    private var tabObservers: [UUID: AnyCancellable] = [:]
    private var lastSite: [UUID: String] = [:]
    /// When Mosaic last set a tab's zoom; Gecko reports soon after are not user changes.
    private var zoomAppliedAt: [UUID: Date] = [:]
    private var audioChecks: [UUID: Task<Void, Never>] = [:]
    private var permissionObserver: NSObjectProtocol?
    private var geckoActivityTask: Task<Void, Never>?
    /// Chromium permission requests waiting for an answer, by identifier.
    var chromiumRequests: [UInt64: MCEFPermissionRequest] = [:]
    /// Chromium tabs using the camera or microphone right now.
    @Published var mediaInUse: [UUID: (video: Bool, audio: Bool)] = [:]

    /// Test runs only (`MOSAIC_FAKE_MEDIA=1`): fake camera, microphone and location in both
    /// engines, so no system permission dialog appears.
    static let usesFakeMedia = ProcessInfo.processInfo.environment["MOSAIC_FAKE_MEDIA"] == "1"

    init(model: BrowserModel) {
        self.model = model
        super.init()
        ChromiumPageRouter.shared.register(self)

        model.$tabs.sink { [weak self] tabs in self?.tabsChanged(tabs) }.store(in: &cancellables)
        model.$selectedTabID.removeDuplicates().sink { [weak self] id in
            // Published before the change lands; act once it has.
            DispatchQueue.main.async { self?.selectionChanged(to: id) }
        }.store(in: &cancellables)

        model.gecko.addSnapshotObserver { [weak self] in self?.geckoSnapshot($0) }
        model.gecko.addStartObserver { [weak self] in await self?.geckoStarted() }
        startGeckoActivityPolling()

        permissionObserver = NotificationCenter.default.addObserver(
            forName: SitePermissionStore.didChangeNotification, object: model.sitePermissions, queue: .main
        ) { [weak self] note in
            guard let site = note.userInfo?["site"] as? String,
                  let permission = note.userInfo?["permission"] as? SitePermissionStore.Permission else { return }
            let decision = (note.userInfo?["entry"] as? SitePermissionStore.Entry)?.decision
            MainActor.assumeIsolated { self?.mirror(permission, decision, site: site) }
        }
    }

    var selectedTab: BrowserTab? { model.selectedTab }

    func tab(withID id: UUID?) -> BrowserTab? {
        guard let id else { return nil }
        return model.tabs.first { $0.id == id }
    }

    func tab(forGeckoID id: Int?) -> BrowserTab? {
        guard let id else { return nil }
        return model.tabs.first { $0.engine == .gecko && $0.geckoID == id }
    }

    /// True when the tab shows a web page an engine can act on.
    func isWebPage(_ tab: BrowserTab?) -> Bool {
        guard let tab, !tab.isStartPage, tab.internalPage == nil, tab.failure == nil else { return false }
        switch tab.engine {
        case .chromium: return tab.chromiumView != nil
        case .gecko: return tab.geckoID != nil && model.gecko.isRunning
        }
    }

    // MARK: - Tabs

    private func tabsChanged(_ tabs: [BrowserTab]) {
        let ids = Set(tabs.map(\.id))
        for tab in tabs where tabObservers[tab.id] == nil {
            observe(tab)
        }
        for id in tabObservers.keys where !ids.contains(id) {
            tabObservers[id] = nil
            lastSite[id] = nil
            zoomAppliedAt[id] = nil
            audioChecks.removeValue(forKey: id)?.cancel()
            dropPageState(forTab: id, reason: .closed)
            if fullScreenTabID == id { endFullScreen() }
            if findTabID == id { hideFindBar() }
        }
    }

    private func observe(_ tab: BrowserTab) {
        let id = tab.id
        let url = tab.$url.removeDuplicates().sink { [weak self, weak tab] url in
            DispatchQueue.main.async { if let tab { self?.urlChanged(tab, url) } }
        }
        let muted = tab.$isMuted.removeDuplicates().dropFirst().sink { [weak self, weak tab] _ in
            DispatchQueue.main.async { if let tab { self?.applyMute(tab) } }
        }
        let gecko = tab.$geckoID.removeDuplicates().sink { [weak self, weak tab] id in
            guard id != nil else { return }
            DispatchQueue.main.async { if let tab { self?.engineAttached(tab) } }
        }
        let engine = tab.$engine.removeDuplicates().dropFirst().sink { [weak self, weak tab] _ in
            DispatchQueue.main.async {
                guard let self, let tab else { return }
                tab.isPlayingAudio = false
                self.dropPageState(forTab: tab.id, reason: .engineSwitched)
                if self.findTabID == tab.id { self.hideFindBar() }
            }
        }
        // Chromium applies its own per-host zoom when a page commits; put the remembered one back.
        let loaded = tab.$isLoading.removeDuplicates().filter { !$0 }.sink { [weak self, weak tab] _ in
            DispatchQueue.main.async {
                guard let self, let tab, tab.engine == .chromium, let view = tab.chromiumView, view.hasBrowser else { return }
                let factor = self.zoomStore.factor(for: tab.url)
                if abs(view.zoomLevel - PageZoom.chromiumLevel(forFactor: factor)) > 0.01 { self.applyZoom(factor, to: tab) }
            }
        }
        tabObservers[id] = AnyCancellable { url.cancel(); muted.cancel(); gecko.cancel(); engine.cancel(); loaded.cancel() }
    }

    /// The tab's page now lives in an engine (a Chromium browser was created, or Gecko
    /// adopted it): bring mute and zoom in line.
    func engineAttached(_ tab: BrowserTab) {
        if Self.usesFakeMedia, tab.engine == .chromium, let view = tab.chromiumView {
            // Test runs: a fixed position, so Chromium never starts macOS location services.
            view.executeDevToolsMethod(
                "Emulation.setGeolocationOverride",
                params: ["latitude": 51.5007, "longitude": -0.1246, "accuracy": 20]) { _, _ in }
        }
        if tab.isMuted { applyMute(tab) }
        applyZoom(zoomStore.factor(for: tab.url), to: tab)
    }

    private func urlChanged(_ tab: BrowserTab, _ url: String) {
        let site = PageZoom.site(for: url) ?? SitePermissionStore.site(for: url) ?? url
        guard lastSite[tab.id] != site else { return }
        let hadSite = lastSite[tab.id] != nil
        lastSite[tab.id] = site
        if hadSite {
            // Prompts and pop-up notices belong to the page that asked.
            dropPageState(forTab: tab.id, reason: .navigated, currentSite: SitePermissionStore.site(for: url))
        }
        let factor = zoomStore.factor(for: url)
        if abs(factor - tab.zoomFactor) > 0.005 || factor != 1 {
            applyZoom(factor, to: tab)
        }
    }

    private func selectionChanged(to id: UUID?) {
        if isFindBarVisible, findTabID != id { hideFindBar() }
        if let fullScreenTabID, fullScreenTabID != id { endFullScreen() }
    }

    // MARK: - Find in page

    /// ⌘F: shows the find bar for the selected tab and focuses it.
    func showFindBar() {
        guard let tab = selectedTab, isWebPage(tab) else {
            NSSound.beep()
            return
        }
        if findTabID != tab.id {
            if let old = self.tab(withID: findTabID) { stopFinding(in: old) }
            findTabID = tab.id
            findStatus = nil
            chromiumFindStarted = false
        }
        isFindBarVisible = true
        findFocusRequest += 1
        if !findText.isEmpty { search(again: false) }
    }

    /// Esc / Done: hides the bar, clears highlights, gives the page focus back.
    func closeFindBar() {
        let tab = tab(withID: findTabID)
        hideFindBar()
        if tab === selectedTab { model.focusContent() }
    }

    private func hideFindBar() {
        if let tab = tab(withID: findTabID) { stopFinding(in: tab) }
        isFindBarVisible = false
        findTabID = nil
        findStatus = nil
        chromiumFindStarted = false
        findGeneration += 1
    }

    /// The text or the case setting changed: search again from the top.
    func findQueryChanged() {
        chromiumFindStarted = false
        search(again: false)
    }

    /// ⌘G / ⇧⌘G and the bar's arrows.
    func findNext(backwards: Bool = false) {
        if !isFindBarVisible || findTabID != selectedTab?.id {
            showFindBar()
            if findText.isEmpty { return }
        }
        search(again: true, backwards: backwards)
    }

    private func search(again: Bool, backwards: Bool = false) {
        guard let tab = tab(withID: findTabID), isWebPage(tab) else { return }
        findGeneration += 1
        let generation = findGeneration
        let text = findText
        guard !text.isEmpty else {
            findStatus = nil
            stopFinding(in: tab)
            return
        }
        switch tab.engine {
        case .chromium:
            guard let view = tab.chromiumView else { return }
            let next = again && chromiumFindStarted
            chromiumFindStarted = true
            guard !next else {
                view.findText(text, forward: !backwards, matchCase: findMatchCase, findNext: true)
                return
            }
            // A new text or case setting: a fresh session. Chromium's first report only counts
            // the matches; `chromiumFindResult` then moves to the first one.
            view.stopFindingAndClearSelection(true)
            chromiumNeedsFirstMatch = true
            view.findText(text, forward: !backwards, matchCase: findMatchCase, findNext: false)
        case .gecko:
            guard let id = tab.geckoID else { return }
            let options: [String: Any] = ["again": again, "backwards": backwards, "caseSensitive": findMatchCase]
            Task {
                guard let json = try? await model.gecko.call("find", id, text, options) as? [String: Any],
                      generation == findGeneration else { return }
                findStatus = FindStatus(
                    current: json["current"] as? Int ?? 0, total: json["total"] as? Int ?? 0,
                    limit: json["limit"] as? Int ?? 0)
            }
        }
    }

    private func stopFinding(in tab: BrowserTab) {
        switch tab.engine {
        case .chromium: tab.chromiumView?.stopFindingAndClearSelection(false)
        case .gecko: if let id = tab.geckoID { model.gecko.send("stopFind", id) }
        }
    }

    /// Chromium's find results (see `ChromiumPageRouter`).
    func chromiumFindResult(_ tab: BrowserTab, count: Int, active: Int, final: Bool) {
        guard tab.id == findTabID, isFindBarVisible else { return }
        findStatus = FindStatus(current: active, total: count)
        if final, count > 0, active == 0, chromiumNeedsFirstMatch, let view = tab.chromiumView {
            // A new search counts matches without making one current; move to the first.
            chromiumNeedsFirstMatch = false
            view.findText(findText, forward: true, matchCase: findMatchCase, findNext: true)
        } else if final {
            chromiumNeedsFirstMatch = false
        }
    }

    // MARK: - Zoom

    func zoomIn() { setZoom(PageZoom.zoomedIn(from: selectedTab?.zoomFactor ?? 1)) }
    func zoomOut() { setZoom(PageZoom.zoomedOut(from: selectedTab?.zoomFactor ?? 1)) }
    func resetZoom() { setZoom(1) }

    /// Zooms the selected page and remembers the zoom for its site (every open tab on the
    /// site follows, in both engines).
    func setZoom(_ factor: Double) {
        guard let tab = selectedTab, isWebPage(tab) else {
            NSSound.beep()
            return
        }
        let value = PageZoom.clamped(factor)
        zoomStore.setFactor(value, for: tab.url)
        let site = PageZoom.site(for: tab.url)
        for other in model.tabs where other === tab || (site != nil && PageZoom.site(for: other.url) == site) {
            applyZoom(value, to: other)
        }
        model.notice = "Zoom \(PageZoom.percentLabel(value))"
    }

    private func applyZoom(_ factor: Double, to tab: BrowserTab) {
        tab.zoomFactor = factor
        zoomAppliedAt[tab.id] = Date()
        switch tab.engine {
        case .chromium:
            tab.chromiumView?.zoomLevel = PageZoom.chromiumLevel(forFactor: factor)
        case .gecko:
            if let id = tab.geckoID { model.gecko.send("setZoom", id, factor) }
        }
    }

    // MARK: - Mute and audio

    private func applyMute(_ tab: BrowserTab) {
        switch tab.engine {
        case .chromium: tab.chromiumView?.audioMuted = tab.isMuted
        case .gecko: if let id = tab.geckoID { model.gecko.send("setMuted", id, tab.isMuted) }
        }
    }

    /// Chromium reported the page became audible. It never reports the opposite, so the
    /// page is asked every couple of seconds whether any media is still playing.
    func chromiumAudioStarted(_ tab: BrowserTab) {
        tab.isPlayingAudio = true
        guard audioChecks[tab.id] == nil else { return }
        let id = tab.id
        audioChecks[id] = Task { [weak self] in
            var quiet = 0
            while !Task.isCancelled, quiet < 2 {
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                guard let self, let tab = self.tab(withID: id), let view = tab.chromiumView else { break }
                quiet = await Self.chromiumIsPlaying(view) ? 0 : quiet + 1
            }
            guard let self else { return }
            self.audioChecks[id] = nil
            self.tab(withID: id)?.isPlayingAudio = false
        }
    }

    /// Media elements playing with sound in the page or its same-origin frames. (Web Audio
    /// that plays without a media element is not visible this way.)
    private static let playingScript = """
    (() => { const playing = d => [...d.querySelectorAll('video,audio')].some(m => !m.paused && !m.ended && \
    !m.muted && m.volume > 0); const docs = [document]; for (const f of document.querySelectorAll('iframe')) \
    { try { if (f.contentDocument) docs.push(f.contentDocument); } catch (e) {} } return docs.some(playing); })()
    """

    private static func chromiumIsPlaying(_ view: MCEFBrowserView) async -> Bool {
        await withCheckedContinuation { continuation in
            view.executeDevToolsMethod(
                "Runtime.evaluate",
                params: ["expression": playingScript, "returnByValue": true, "silent": true]
            ) { result, _ in
                let value = (result?["result"] as? [String: Any])?["value"] as? Bool
                continuation.resume(returning: value ?? false)
            }
        }
    }

    // MARK: - Gecko

    private func geckoStarted() async {
        _ = try? await model.gecko.call("configurePageFeatures", ["fakeMedia": Self.usesFakeMedia])
        let entries = permissions.entries().compactMap { entry -> [String: Any]? in
            guard [.location, .notifications, .popups].contains(entry.permission) else { return nil }
            return ["origin": entry.site, "permission": entry.permission.rawValue, "decision": entry.decision.rawValue]
        }
        if !entries.isEmpty { _ = try? await model.gecko.call("setSitePermissions", entries) }
    }

    private func geckoSnapshot(_ snapshot: GeckoEngine.Snapshot) {
        for event in snapshot.events { geckoEvent(event) }
    }

    /// Once a second while Gecko runs: zoom and mute of its tabs. (Gecko's audio state is
    /// reported by the tabs area.)
    private func startGeckoActivityPolling() {
        geckoActivityTask?.cancel()
        geckoActivityTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                guard let self, self.model.gecko.isRunning else { continue }
                guard let list = try? await self.model.gecko.call("pageActivity") as? [[String: Any]] else { continue }
                for entry in list {
                    guard let tab = self.tab(forGeckoID: entry["id"] as? Int) else { continue }
                    if let muted = entry["muted"] as? Bool, muted != tab.isMuted {
                        // Mosaic decides; put Gecko back in line if something else changed it.
                        self.applyMute(tab)
                    }
                    if let zoom = entry["zoom"] as? Double, abs(zoom - tab.zoomFactor) > 0.005, !tab.isLoading,
                       Date().timeIntervalSince(self.zoomAppliedAt[tab.id] ?? .distantPast) > 1.5
                    {
                        // Zoomed inside the Gecko window (its own ⌘+ / ⌘- / pinch): remember that too.
                        tab.zoomFactor = zoom
                        self.zoomStore.setFactor(zoom, for: tab.url)
                    }
                }
            }
        }
    }

    private func geckoEvent(_ event: GeckoEngine.Event) {
        let detail = event.detail.flatMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) } as? [String: Any] ?? [:]
        switch event.type {
        case "find":
            activateForKeyboard()
            showFindBar()
        case "findNext", "findPrevious":
            activateForKeyboard()
            findNext(backwards: event.type == "findPrevious")
        case "permissionRequest":
            guard let tab = tab(forGeckoID: event.tab), let id = detail["id"] as? Int else { return }
            let request = PermissionRequestInfo(
                origin: detail["origin"] as? String ?? "",
                kinds: (detail["kinds"] as? [String] ?? []).compactMap(PermissionRequestInfo.Kind.init(rawValue:)),
                otherNames: detail["other"] as? [String] ?? [])
            handlePermissionRequest(request, in: tab, token: .gecko(id))
        case "permissionCancel":
            if let id = detail["id"] as? Int { cancelPermissionPrompt(token: .gecko(id)) }
        case "popupBlocked":
            guard let tab = tab(forGeckoID: event.tab) else { return }
            popupsBlocked(in: tab, urls: detail["urls"] as? [String] ?? [], count: detail["count"] as? Int ?? 1, replacing: true)
        case "pageFullscreen":
            let entering = detail["value"] as? Bool ?? false
            let tab = tab(forGeckoID: event.tab) ?? selectedTab
            if entering { fullScreenTabID = tab?.id } else if fullScreenTabID == tab?.id || tab == nil { fullScreenTabID = nil }
        default:
            break
        }
    }

    /// A shortcut pressed inside the Gecko window opens Mosaic UI, which needs the keyboard.
    func activateForKeyboard() {
        if model.allowsFocusChanges { NSApp.activate(ignoringOtherApps: true) }
    }

    // MARK: - Printing, PDF, developer tools, Picture in Picture

    func print() {
        guard let tab = selectedTab, isWebPage(tab) else { return NSSound.beep() }
        switch tab.engine {
        case .chromium: tab.chromiumView?.printPage()
        case .gecko:
            if let id = tab.geckoID { model.gecko.send("print", id) }
            activateGeckoForDialog()
        }
    }

    /// File › Export as PDF…: asks where, then saves.
    func exportPDF() {
        guard let tab = selectedTab, isWebPage(tab) else { return NSSound.beep() }
        askForDestination(name: Self.fileName(for: tab, suffix: nil), type: .pdf) { [weak self] url in
            self?.exportPDF(of: tab, to: url)
        }
    }

    func exportPDF(of tab: BrowserTab, to url: URL, completion: ((Bool) -> Void)? = nil) {
        let finish: (Bool) -> Void = { [weak self] ok in
            self?.model.notice = ok ? "Saved “\(url.lastPathComponent)”" : "Couldn’t save the PDF."
            completion?(ok)
        }
        switch tab.engine {
        case .chromium:
            guard let view = tab.chromiumView else { return finish(false) }
            view.printToPDF(url.path) { ok in MainActor.assumeIsolated { finish(ok) } }
        case .gecko:
            guard let id = tab.geckoID else { return finish(false) }
            Task {
                do {
                    try await model.gecko.call("saveAsPDF", id, url.path)
                    finish(FileManager.default.fileExists(atPath: url.path))
                } catch {
                    finish(false)
                }
            }
        }
    }

    func toggleDevTools() {
        guard let tab = selectedTab, isWebPage(tab) else { return NSSound.beep() }
        switch tab.engine {
        case .chromium:
            guard let view = tab.chromiumView else { return }
            if view.hasDevTools { view.closeDevTools() } else { view.showDevTools() }
        case .gecko:
            if let id = tab.geckoID { model.gecko.send("toggleDevTools", id) }
        }
    }

    /// Puts the page's video into Picture in Picture (or brings it back). Both engines pick
    /// the focused video, else the one playing, else the longest.
    func togglePictureInPicture() {
        guard let tab = selectedTab, isWebPage(tab) else { return NSSound.beep() }
        switch tab.engine {
        case .gecko:
            if let id = tab.geckoID { model.gecko.send("togglePictureInPicture", id) }
        case .chromium:
            tab.chromiumView?.executeDevToolsMethod(
                "Runtime.evaluate",
                params: ["expression": Self.pictureInPictureScript, "userGesture": true, "awaitPromise": true,
                         "returnByValue": true]
            ) { [weak self] result, _ in
                let value = (result?["result"] as? [String: Any])?["value"] as? String
                MainActor.assumeIsolated {
                    if value == "none" { self?.model.notice = "This page has no video to show in Picture in Picture." }
                }
            }
        }
    }

    private static let pictureInPictureScript = """
    (async () => { if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return 'left'; }     const videos = [...document.querySelectorAll('video')].filter(v => !isNaN(v.duration));     const video = (document.activeElement instanceof HTMLVideoElement && document.activeElement) ||     videos.find(v => !v.paused) || videos.sort((a, b) => b.duration - a.duration)[0];     if (!video) return 'none'; await video.requestPictureInPicture(); return 'entered'; })()
    """

    /// Firefox's print dialog lives in the Gecko window, which needs the keyboard for it.
    private func activateGeckoForDialog() {
        guard model.allowsFocusChanges else { return }
        Task {
            try? await Task.sleep(nanoseconds: 300_000_000)
            model.gecko.activate()
        }
    }

    // MARK: - Screenshots

    enum ScreenshotDestination {
        case ask
        case file(URL)
        case clipboard
    }

    /// Captures the visible area or the whole page of the selected tab.
    func takeScreenshot(fullPage: Bool, to destination: ScreenshotDestination,
                        completion: ((Data?) -> Void)? = nil) {
        guard let tab = selectedTab, isWebPage(tab) else {
            NSSound.beep()
            completion?(nil)
            return
        }
        capture(tab, fullPage: fullPage) { [weak self] png, cropped in
            guard let self else { return }
            guard let png else {
                self.model.notice = "Couldn’t take a screenshot of this page."
                completion?(nil)
                return
            }
            let note = cropped ? " (cut off: the page is very long)" : ""
            switch destination {
            case .clipboard:
                let board = NSPasteboard.general
                board.clearContents()
                board.setData(png, forType: .png)
                if let image = NSImage(data: png), let tiff = image.tiffRepresentation { board.setData(tiff, forType: .tiff) }
                self.model.notice = "Screenshot copied" + note
                completion?(png)
            case .file(let url):
                let ok = (try? png.write(to: url, options: .atomic)) != nil
                self.model.notice = ok ? "Saved “\(url.lastPathComponent)”" + note : "Couldn’t save the screenshot."
                completion?(ok ? png : nil)
            case .ask:
                self.askForDestination(name: Self.fileName(for: tab, suffix: fullPage ? "full page" : nil), type: .png) { url in
                    let ok = (try? png.write(to: url, options: .atomic)) != nil
                    self.model.notice = ok ? "Saved “\(url.lastPathComponent)”" + note : "Couldn’t save the screenshot."
                    completion?(ok ? png : nil)
                }
            }
        }
    }

    /// Tallest page a full-page screenshot covers, in CSS pixels.
    nonisolated static let maxScreenshotHeight = 16384

    private func capture(_ tab: BrowserTab, fullPage: Bool, completion: @escaping (Data?, Bool) -> Void) {
        switch tab.engine {
        case .chromium:
            guard let view = tab.chromiumView else { return completion(nil, false) }
            let shoot: ([String: Any], Bool) -> Void = { params, cropped in
                view.executeDevToolsMethod("Page.captureScreenshot", params: params) { result, _ in
                    let data = (result?["data"] as? String).flatMap { Data(base64Encoded: $0) }
                    completion(data, cropped)
                }
            }
            guard fullPage else { return shoot(["format": "png"], false) }
            view.executeDevToolsMethod("Page.getLayoutMetrics", params: nil) { metrics, _ in
                let size = metrics?["cssContentSize"] as? [String: Any]
                let width = (size?["width"] as? Double) ?? 0
                let height = (size?["height"] as? Double) ?? 0
                guard width > 0, height > 0 else { return shoot(["format": "png"], false) }
                let shown = min(height, Double(Self.maxScreenshotHeight))
                shoot([
                    "format": "png", "captureBeyondViewport": true,
                    "clip": ["x": 0, "y": 0, "width": width, "height": shown, "scale": 1],
                ], height > shown)
            }
        case .gecko:
            guard let id = tab.geckoID else { return completion(nil, false) }
            let file = FileManager.default.temporaryDirectory
                .appendingPathComponent("mosaic-screenshot-\(UUID().uuidString).png")
            Task {
                let info = try? await model.gecko.call("screenshot", id, file.path, fullPage) as? [String: Any]
                let data = try? Data(contentsOf: file)
                try? FileManager.default.removeItem(at: file)
                completion(info == nil ? nil : data, info?["cropped"] as? Bool ?? false)
            }
        }
    }

    // MARK: - Saving

    private static func fileName(for tab: BrowserTab, suffix: String?) -> String {
        var base = tab.displayTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        if base.isEmpty { base = "Page" }
        base = base.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        if base.count > 80 { base = String(base.prefix(80)) }
        if let suffix { base += " (\(suffix))" }
        return base
    }

    /// The save panel, as a sheet on Mosaic's window.
    private func askForDestination(name: String, type: UTType, then save: @escaping (URL) -> Void) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [type]
        panel.nameFieldStringValue = name + "." + (type.preferredFilenameExtension ?? "")
        panel.canCreateDirectories = true
        panel.directoryURL = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
        let handler: (NSApplication.ModalResponse) -> Void = { response in
            guard response == .OK, let url = panel.url else { return }
            save(url)
        }
        if let window = NSApp.windows.first(where: { $0.title == "Mosaic" && $0.isVisible }) {
            panel.beginSheetModal(for: window, completionHandler: handler)
        } else {
            panel.begin(completionHandler: handler)
        }
    }
}
