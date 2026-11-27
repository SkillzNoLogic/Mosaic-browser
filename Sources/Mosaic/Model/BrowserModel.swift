import AppKit
import CEFBridge
import Combine
import MosaicCore

/// The browser's state: tabs, the selected search engine and rendering engine,
/// and the glue between Mosaic's UI and the two engines.
@MainActor
final class BrowserModel: ObservableObject {
    @Published private(set) var tabs: [BrowserTab] = []
    @Published private(set) var selectedTabID: UUID?
    /// Engine used for new tabs. Mirrors the selected tab's engine.
    @Published private(set) var engine: EngineKind
    @Published var searchEngine: SearchEngine {
        didSet { AppPaths.defaults.set(searchEngine.id, forKey: Keys.searchEngine) }
    }
    @Published var addressText = ""
    /// Incremented to ask the toolbar to focus the address field.
    @Published private(set) var addressFocusRequest = 0
    @Published var showExtensions = false
    @Published var showBlocker = false

    /// The settings category shown inside the window, or nil when settings are closed.
    @Published var settingsCategory: SettingsCategory? {
        didSet { if (oldValue == nil) != (settingsCategory == nil) { updateDocking() } }
    }

    // Workspaces and groups: shapes in TabOrganization.swift, behaviour owned by the tabs area.
    @Published var workspaces: [Workspace] = [.personal]
    @Published var selectedWorkspaceID: UUID = Workspace.personal.id
    @Published var groups: [TabGroup] = []
    @Published var notice: String?

    let gecko = GeckoEngine()
    let extensions: ExtensionManager
    let blocker: ContentBlocker
    let library = Library()
    let chromiumAvailable: Bool

    // MARK: Feature services
    // Feature areas add their service objects here, one line each (see docs/AGENTS.md).
    let passwords = PasswordManager()  // passwords: Keychain store, save prompt, fill (Features/Passwords)

    /// True while the user is typing in the address field; page-driven URL
    /// changes must not overwrite what they are typing.
    var isEditingAddress = false

    /// Scripted runs must not pull keyboard focus away from whatever the person
    /// at the Mac is doing; see `Automation`.
    var allowsFocusChanges = true

    private var pendingGeckoOpens = 0
    /// Gecko tabs to switch to as soon as they show up in a snapshot.
    private var selectWhenAdopted: Set<Int> = []
    /// The tab Gecko reported as selected in the previous snapshot.
    private var lastGeckoSelection: Int?
    /// Gecko tabs Mosaic has asked to close; a snapshot taken just before the
    /// close lands must not resurrect them. Agent tab IDs are never reused.
    private var closedGeckoIDs: Set<Int> = []
    private var lastLocalSelection = Date.distantPast
    private var contentRect: CGRect?
    private var windowIsVisible = true
    private var cancellables: Set<AnyCancellable> = []

    private enum Keys {
        static let searchEngine = "searchEngine"
        static let engine = "engine"
    }

    init(chromiumAvailable: Bool) {
        self.chromiumAvailable = chromiumAvailable
        let defaults = AppPaths.defaults
        searchEngine = defaults.string(forKey: Keys.searchEngine).flatMap(SearchEngine.named) ?? .default
        // Mozilla is the default engine.
        let saved = defaults.string(forKey: Keys.engine).flatMap(EngineKind.init(rawValue:)) ?? .gecko
        engine = (saved == .chromium && !chromiumAvailable) ? .gecko : saved
        extensions = ExtensionManager(gecko: gecko)
        blocker = ContentBlocker(gecko: gecko)

        gecko.onSnapshot = { [weak self] in self?.reconcile(with: $0) }
        gecko.onStopped = { [weak self] in self?.geckoStopped() }
        gecko.onStarted = { [weak self] in
            // The blocker first, so the very first page is already filtered.
            await self?.blocker.configureGecko()
            await self?.extensions.loadAllIntoGecko()
        }
        // Re-render dependents when the engine's or the blocker's status changes.
        gecko.objectWillChange.sink { [weak self] in self?.objectWillChange.send() }.store(in: &cancellables)
        blocker.objectWillChange.sink { [weak self] in self?.objectWillChange.send() }.store(in: &cancellables)
        blocker.start()
        passwords.attach(to: self)  // passwords: connects to both engines

        newTab()
    }

    var selectedTab: BrowserTab? { tabs.first { $0.id == selectedTabID } }

    /// Shows settings inside the window, at `category`.
    func openSettings(_ category: SettingsCategory = .general) {
        settingsCategory = category
    }

    func closeSettings() {
        settingsCategory = nil
    }

    /// Opens one of Mosaic's own pages, reusing a tab that already shows it.
    func openInternalPage(_ page: InternalPage) {
        if let existing = tabs.first(where: { $0.internalPage == page }) {
            select(existing)
        } else {
            newTab(url: page.url)
        }
    }

    // MARK: - Tabs

    @discardableResult
    func newTab(url: String = "", engine requested: EngineKind? = nil, select: Bool = true) -> BrowserTab {
        let tab = makeTab(engine: requested ?? engine)
        if let current = selectedTab, let index = tabs.firstIndex(of: current) {
            tabs.insert(tab, at: index + 1)
        } else {
            tabs.append(tab)
        }
        if select { self.select(tab) }
        if url.isEmpty {
            if select { focusAddressBar() }
        } else {
            load(url, in: tab)
        }
        return tab
    }

    private func makeTab(engine: EngineKind) -> BrowserTab {
        let tab = BrowserTab(engine: engine)
        tab.onChange = { [weak self] in self?.tabChanged($0) }
        tab.onNewTabRequest = { [weak self] source, url in
            self?.newTab(url: url, engine: source.engine)
        }
        return tab
    }

    func select(_ tab: BrowserTab) {
        guard tabs.contains(tab) else { return }
        selectedTabID = tab.id
        lastLocalSelection = Date()
        setEnginePreference(tab.engine)
        isEditingAddress = false
        addressText = tab.url
        if tab.engine == .gecko, let id = tab.geckoID {
            gecko.send("selectTab", id)
        }
        updateDocking()
    }

    func close(_ tab: BrowserTab) {
        guard let index = tabs.firstIndex(of: tab) else { return }
        detachFromEngine(tab)
        tabs.remove(at: index)
        if tabs.isEmpty {
            newTab()
        } else if selectedTabID == tab.id {
            select(tabs[min(index, tabs.count - 1)])
        }
    }

    func closeSelectedTab() {
        if let tab = selectedTab { close(tab) }
    }

    func selectAdjacentTab(offset: Int) {
        guard let tab = selectedTab, let index = tabs.firstIndex(of: tab), tabs.count > 1 else { return }
        select(tabs[(index + offset + tabs.count) % tabs.count])
    }

    private func tabChanged(_ tab: BrowserTab) {
        guard tab.id == selectedTabID else { return }
        // The toolbar reads the selected tab's state through the model.
        objectWillChange.send()
        if !isEditingAddress, addressText != tab.url {
            addressText = tab.url
        }
    }

    // MARK: - Navigation

    /// Handles whatever was submitted in the address bar or the start page.
    func navigate(_ input: String) {
        guard let url = AddressResolver.resolve(input, searchEngine: searchEngine) else { return }
        let tab = selectedTab ?? newTab()
        isEditingAddress = false
        load(url.absoluteString, in: tab)
        focusContent()
    }

    func load(_ url: String, in tab: BrowserTab) {
        // Mosaic's own pages are drawn by the shell; no engine is involved.
        if let page = InternalPage(url: url) {
            // Otherwise the engine's next report would put the old page's address back.
            detachFromEngine(tab)
            tab.canGoBack = false
            tab.canGoForward = false
            tab.url = page.url
            tab.title = page.title
            tab.failure = nil
            tab.isLoading = false
            tabChanged(tab)
            updateDocking()
            return
        }
        tab.url = url
        tab.title = ""
        tab.failure = nil
        tab.isLoading = true
        tab.lastLoadRequest = Date()
        tabChanged(tab)

        switch tab.engine {
        case .chromium:
            guard chromiumAvailable else {
                tab.isLoading = false
                tab.failure = "The Chromium engine is not bundled in this build. Run Scripts/bootstrap.sh and rebuild."
                return
            }
            if let view = tab.chromiumView {
                view.loadURL(url)
            } else {
                let view = MCEFBrowserView(url: url)
                view.delegate = tab
                tab.chromiumView = view
                objectWillChange.send()
            }
        case .gecko:
            loadInGecko(url, tab)
        }
        updateDocking()
    }

    private func loadInGecko(_ url: String, _ tab: BrowserTab) {
        let previous = tab.geckoWork
        tab.geckoWork = Task { [weak self, weak tab] in
            await previous?.value
            guard let self, let tab else { return }
            do {
                try await self.gecko.start()
                // The tab may have been closed, moved to Chromium, or sent
                // somewhere else while Gecko was starting.
                guard self.tabs.contains(tab), tab.engine == .gecko, tab.url == url else { return }
                if let id = tab.geckoID {
                    try await self.gecko.call("navigate", id, url)
                } else {
                    self.pendingGeckoOpens += 1
                    defer { self.pendingGeckoOpens -= 1 }
                    let selected = tab.id == self.selectedTabID
                    let id = try await self.gecko.call("openTab", url, selected) as? Int
                    guard self.tabs.contains(tab), tab.engine == .gecko else {
                        if let id {
                            self.closedGeckoIDs.insert(id)
                            self.gecko.send("closeTab", id)
                        }
                        return
                    }
                    tab.geckoID = id
                }
                self.updateDocking()
            } catch {
                guard self.tabs.contains(tab), tab.engine == .gecko else { return }
                tab.isLoading = false
                tab.failure = error.localizedDescription
            }
        }
    }

    func goBack() {
        guard let tab = selectedTab else { return }
        switch tab.engine {
        case .chromium: tab.chromiumView?.goBack()
        case .gecko: if let id = tab.geckoID { gecko.send("goBack", id) }
        }
    }

    func goForward() {
        guard let tab = selectedTab else { return }
        switch tab.engine {
        case .chromium: tab.chromiumView?.goForward()
        case .gecko: if let id = tab.geckoID { gecko.send("goForward", id) }
        }
    }

    func reload() {
        guard let tab = selectedTab, !tab.isStartPage else { return }
        switch tab.engine {
        case .chromium:
            if let view = tab.chromiumView { view.reload() } else { load(tab.url, in: tab) }
        case .gecko:
            if let id = tab.geckoID, gecko.isRunning { gecko.send("reload", id) } else { load(tab.url, in: tab) }
        }
    }

    func stopLoading() {
        guard let tab = selectedTab else { return }
        switch tab.engine {
        case .chromium: tab.chromiumView?.stopLoading()
        case .gecko: if let id = tab.geckoID { gecko.send("stop", id) }
        }
    }

    /// `activating` pulls Mosaic to the front first; needed when the request
    /// comes from inside the Gecko window (⌘L pressed there).
    func focusAddressBar(activating: Bool = false) {
        if activating, allowsFocusChanges { NSApp.activate(ignoringOtherApps: true) }
        addressFocusRequest += 1
    }

    /// Hands keyboard focus to the page of the selected tab.
    func focusContent() {
        guard let tab = selectedTab, !tab.isStartPage else { return }
        switch tab.engine {
        case .chromium:
            tab.chromiumView?.focusContent()
        case .gecko:
            // Give Gecko a beat to exist/dock before it takes focus.
            Task {
                try? await Task.sleep(nanoseconds: 150_000_000)
                guard self.allowsFocusChanges, self.selectedTab === tab, tab.engine == .gecko,
                      tab.geckoID != nil, !self.isEditingAddress else { return }
                self.gecko.send("focusContent")
                self.gecko.activate()
            }
        }
    }

    // MARK: - Engine switching

    /// Moves the selected tab to `kind` (reloading its page there) and makes
    /// `kind` the engine for new tabs.
    func setEngine(_ kind: EngineKind) {
        if kind == .chromium, !chromiumAvailable {
            notice = "The Chromium engine is not bundled in this build. Run Scripts/bootstrap.sh and rebuild."
            return
        }
        setEnginePreference(kind, remember: true)
        guard let tab = selectedTab, tab.engine != kind else { return }
        let url = tab.url
        detachFromEngine(tab)
        tab.engine = kind
        tab.canGoBack = false
        tab.canGoForward = false
        tab.isLoading = false
        tab.failure = nil
        tab.blockedCount = 0
        if !url.isEmpty { load(url, in: tab) }
        updateDocking()
    }

    func toggleEngine() {
        setEngine((selectedTab?.engine ?? engine).other)
    }

    /// `engine` follows the selected tab so a new tab opens next to its
    /// neighbours' engine. Only an explicit switch (`remember`) changes what
    /// Mosaic starts with next time.
    private func setEnginePreference(_ kind: EngineKind, remember: Bool = false) {
        if engine != kind { engine = kind }
        if remember { AppPaths.defaults.set(kind.rawValue, forKey: Keys.engine) }
    }

    func cycleSearchEngine() {
        searchEngine = searchEngine.next
        notice = "Search engine: \(searchEngine.name)"
    }

    /// Releases whatever the tab holds in its current engine.
    private func detachFromEngine(_ tab: BrowserTab) {
        if let view = tab.chromiumView {
            view.closeBrowser()
            tab.chromiumView = nil
        }
        tab.geckoWork?.cancel()
        tab.geckoWork = nil
        if let id = tab.geckoID {
            tab.geckoID = nil
            closedGeckoIDs.insert(id)
            gecko.send("closeTab", id)
        }
    }

    // MARK: - Gecko mirroring

    /// Brings Mosaic's tab list in line with what Gecko reports: page-driven
    /// navigations, tabs Gecko opened itself (target=_blank, extension pages),
    /// and tabs the page closed.
    private func reconcile(with snapshot: GeckoEngine.Snapshot) {
        var byGeckoID: [Int: BrowserTab] = [:]
        for tab in tabs where tab.engine == .gecko {
            if let id = tab.geckoID { byGeckoID[id] = tab }
        }
        let reported = Set(snapshot.tabs.map(\.id))

        for state in snapshot.tabs where !closedGeckoIDs.contains(state.id) {
            if let tab = byGeckoID[state.id] {
                apply(state, to: tab)
            } else if pendingGeckoOpens == 0 {
                let tab = makeTab(engine: .gecko)
                tab.geckoID = state.id
                apply(state, to: tab)
                if let current = selectedTab, let index = tabs.firstIndex(of: current) {
                    tabs.insert(tab, at: index + 1)
                } else {
                    tabs.append(tab)
                }
                byGeckoID[state.id] = tab
                // Show it if the user is on a Gecko page (they clicked a link
                // that opens a tab) or asked for it; a tab Gecko opens on its
                // own, such as an extension's welcome page, stays in the background.
                let requested = selectWhenAdopted.remove(state.id) != nil
                let onGeckoPage = selectedTab.map { $0.engine == .gecko && $0.geckoID != nil } ?? false
                if requested || (state.selected && onGeckoPage) { selectLocally(tab) }
            }
        }

        if pendingGeckoOpens == 0 {
            for (id, tab) in byGeckoID where !reported.contains(id) {
                tab.geckoID = nil
                close(tab)
            }
        }

        // Follow a tab switch made inside Gecko (a page focusing its opener,
        // an extension activating a tab). Only a *change* in Gecko's selection
        // counts, and only while a live Gecko tab is showing here; otherwise a
        // blank or Chromium tab the user just opened would be yanked away.
        let geckoSelection = snapshot.tabs.first(where: \.selected)?.id
        defer { lastGeckoSelection = geckoSelection }
        if let geckoSelection, geckoSelection != lastGeckoSelection, lastGeckoSelection != nil,
           Date().timeIntervalSince(lastLocalSelection) > 0.6,
           let current = selectedTab, current.engine == .gecko, current.geckoID != nil,
           let selected = byGeckoID[geckoSelection], selected !== current
        {
            selectLocally(selected)
        }

        for event in snapshot.events { handle(event) }
    }

    private func apply(_ state: GeckoEngine.TabState, to tab: BrowserTab) {
        // Just after a load is requested Gecko still reports the blank page it
        // started from; don't let that flash into the address bar.
        let settling = state.url == "about:blank" && !tab.url.isEmpty
            && (state.loading || Date().timeIntervalSince(tab.lastLoadRequest) < 2)
        if !settling {
            if tab.url != state.url { tab.url = state.url }
            if tab.title != state.title { tab.title = state.title }
        }
        if tab.isLoading != state.loading, !settling || state.loading { tab.isLoading = state.loading }
        if tab.canGoBack != state.canGoBack { tab.canGoBack = state.canGoBack }
        if tab.canGoForward != state.canGoForward { tab.canGoForward = state.canGoForward }
        if tab.blockedCount != state.blocked { tab.blockedCount = state.blocked }
        tabChanged(tab)
    }

    /// Selects a tab Gecko already made current (no command sent back).
    private func selectLocally(_ tab: BrowserTab) {
        selectedTabID = tab.id
        setEnginePreference(tab.engine)
        isEditingAddress = false
        addressText = tab.url
        updateDocking()
    }

    private func handle(_ event: GeckoEngine.Event) {
        switch event.type {
        case "focusAddress": focusAddressBar(activating: true)
        case "newTab":
            if allowsFocusChanges { NSApp.activate(ignoringOtherApps: true) }
            newTab()
        case "closeTab": closeSelectedTab()
        case "toggleEngine": toggleEngine()
        case "toggleBlocker": toggleBlocker()
        case "passwords": if let message = event.passwords { passwords.handleGecko(message) }  // passwords
        default: break
        }
    }

    private func geckoStopped() {
        closedGeckoIDs.removeAll()
        lastGeckoSelection = nil
        for tab in tabs where tab.engine == .gecko {
            tab.geckoID = nil
            tab.geckoWork = nil
            tab.isLoading = false
            if !tab.isStartPage { tab.failure = "Gecko stopped. Reload to start it again." }
        }
        updateDocking()
    }

    // MARK: - Docking

    /// The content area's rect on screen (AppKit coordinates), reported by the
    /// view layer whenever the window moves or resizes.
    func setContentRect(_ rect: CGRect?) {
        contentRect = rect
        updateDocking()
    }

    func setWindowVisible(_ visible: Bool) {
        windowIsVisible = visible
        updateDocking()
    }

    private func updateDocking() {
        var rect: GeckoRect?
        if let area = contentRect, area.width >= 50, area.height >= 50, let primary = NSScreen.screens.first {
            // AppKit measures from the bottom-left of the primary display, Gecko from its top-left.
            rect = GeckoRect(
                x: Int(area.minX.rounded()),
                y: Int((primary.frame.maxY - area.maxY).rounded()),
                width: Int(area.width.rounded()),
                height: Int(area.height.rounded()))
        }
        gecko.dock(rect, shown: windowIsVisible && showsGeckoPage)
    }

    /// True when the selected tab is a live Gecko page, i.e. the content area
    /// is the transparent hole rather than one of Mosaic's own views.
    var showsGeckoPage: Bool {
        // Settings and Mosaic's own pages cover the content area, so the Gecko window
        // behind it must not show through.
        guard let tab = selectedTab, settingsCategory == nil, tab.internalPage == nil else { return false }
        return tab.engine == .gecko && !tab.isStartPage && tab.failure == nil
            && tab.geckoID != nil && gecko.isRunning
    }

    /// True when Gecko's window is what the user sees in the content area.
    var isGeckoDocked: Bool { showsGeckoPage && windowIsVisible && contentRect != nil }

    // MARK: - Content blocker

    /// Master switch. The page in front is reloaded so the change is visible.
    func setBlockerEnabled(_ enabled: Bool) {
        guard blocker.isEnabled != enabled else { return }
        blocker.setEnabled(enabled)
        notice = enabled ? "Content blocker on" : "Content blocker off"
        reload()
    }

    func toggleBlocker() {
        setBlockerEnabled(!blocker.isEnabled)
    }

    /// Per-site switch for the site of the selected tab.
    func setBlocking(_ blocking: Bool, onSiteOf tab: BrowserTab) {
        blocker.setBlocking(blocking, onSiteOf: tab.url)
        if tab === selectedTab { reload() }
    }

    // MARK: - Extensions

    /// Chrome Web Store extension ID if the selected tab is showing a listing.
    var webStoreExtensionID: String? {
        guard let url = selectedTab?.url, url.contains("/detail/") else { return nil }
        return ChromeWebStore.extensionID(from: url)
    }

    func openExtensionPage(_ ext: InstalledExtension, kind: String) {
        Task {
            do {
                try await gecko.start()
                await extensions.ensureLoaded(ext)
                // The agent opens the page in a new Gecko tab; `reconcile` adopts it.
                guard let id = try await gecko.call("openExtensionPage", ext.geckoID, kind) as? Int else {
                    notice = "\(ext.name) has no \(kind) page."
                    return
                }
                showExtensions = false
                if let tab = tabs.first(where: { $0.geckoID == id }) {
                    selectLocally(tab)
                } else {
                    selectWhenAdopted.insert(id)
                }
            } catch {
                notice = error.localizedDescription
            }
        }
    }

    func shutdown() {
        gecko.stopAndWait()
    }
}
