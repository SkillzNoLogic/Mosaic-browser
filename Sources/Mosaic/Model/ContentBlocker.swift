import AppKit
import CEFBridge
import MosaicCore

/// Mosaic's built-in ad and tracker blocker.
///
/// It is part of the browser, not an extension: uBlock Origin's filtering
/// engine, built from uBO's source, with uBO's filter lists. The same engine and
/// lists serve both rendering engines:
///
/// - Chromium tabs: Chromium asks `RequestFilter` (an engine hosted in this
///   process) before each request, and runs a style-injection script built
///   from the engine's cosmetic filters in each frame it loads.
/// - Mozilla tabs: the Gecko agent loads the engine inside Gecko, cancels
///   blocked requests there (`configureBlocker` in `agent.sys.mjs`) and adds
///   the cosmetic stylesheets to each document (`configurePrivacy`).
///
/// What it does not do: procedural cosmetic filters (`:has-text()` and the
/// like) and scriptlet injection (`##+js(...)`), which in uBO need its content
/// scripts.
@MainActor
final class ContentBlocker: ObservableObject {
    enum Status: Equatable {
        /// The engine was not bundled into this build.
        case unavailable
        case loading
        case ready(filters: Int)
        case failed(String)
    }

    /// One blocked request, for the dashboard.
    struct BlockedRequest: Identifiable, Hashable {
        let url: String
        let type: String
        let count: Int
        /// The filter that matched, as uBO's logger writes it.
        let filter: String
        /// The list the filter comes from, when it could be found.
        let list: String?

        var id: String { type + " " + url }
        var host: String { URLComponents(string: url)?.host ?? url }
    }

    @Published private(set) var status: Status = .loading
    @Published private(set) var settings: BlockerSettings
    @Published private(set) var isUpdating = false
    /// When the filter lists were last refreshed.
    @Published private(set) var listsDate: Date?
    /// Version of uBlock Origin the engine was built from.
    @Published private(set) var engineVersion: String?
    /// Hide page elements named by cosmetic filters (`##.ad`).
    @Published private(set) var cosmeticEnabled: Bool
    @Published private(set) var cosmeticStats = BlockerEngine.CosmeticStats()
    /// IDs of the filter lists in use (see `FilterListCatalog.all`).
    @Published private(set) var enabledListIDs: Set<String>
    /// The user's own filters ("My filters").
    @Published private(set) var userFilters: String
    /// Result of the last list update, for the settings page.
    @Published private(set) var lastUpdateMessage: String?

    private let filter = RequestFilter()
    private(set) var store: FilterListStore
    private unowned let gecko: GeckoEngine
    private var buildGeneration = 0
    /// The engine Chromium tabs use; also answers dashboard and validation questions.
    private var engine: BlockerEngine?
    private var cosmeticCache: CosmeticStyleCache?
    /// Requests blocked in Chromium tabs, per page.
    let chromiumLog = BlockLog()

    /// Lists older than this are refreshed in the background.
    private static let refreshInterval: TimeInterval = 2 * 24 * 60 * 60

    private enum Keys {
        static let enabled = "blockerEnabled"
        static let allowlist = "blockerAllowlist"
        static let cosmetic = "blockerCosmetic"
        static let lists = "blockerLists"
    }

    /// `ublock-engine.js`, the list snapshot and uBO's licence, inside the app.
    private static var resources: URL? {
        Bundle.main.resourceURL?.appendingPathComponent("blocker", isDirectory: true)
    }

    private static var engineBundle: URL? {
        resources?.appendingPathComponent("ublock-engine.js")
    }

    init(gecko: GeckoEngine) {
        self.gecko = gecko
        let defaults = AppPaths.defaults
        settings = BlockerSettings(
            isEnabled: defaults.object(forKey: Keys.enabled) as? Bool ?? true,
            allowlist: defaults.stringArray(forKey: Keys.allowlist) ?? [])
        cosmeticEnabled = defaults.object(forKey: Keys.cosmetic) as? Bool ?? true
        let known = Set(FilterListCatalog.all.map(\.id))
        let saved = defaults.stringArray(forKey: Keys.lists).map { Set($0).intersection(known) }
        let enabled = saved ?? Set(FilterListCatalog.defaults.map(\.id))
        enabledListIDs = enabled
        store = FilterListStore(directory: AppPaths.blockerLists, lists: FilterListCatalog.lists(enabled: enabled))
        userFilters = store.userFilters()
        filter.use(settings: settings)
    }

    // MARK: - Lifecycle

    /// Hooks the blocker into Chromium and loads the engine in the background.
    /// Call once at launch, before any tab exists.
    func start() {
        guard let bundle = Self.engineBundle, let resources = Self.resources,
              FileManager.default.fileExists(atPath: bundle.path)
        else {
            status = .unavailable
            return
        }
        let filter = self.filter
        let log = chromiumLog
        MosaicCEF.requestFilter = { url, documentURL, type in
            let blocked = filter.shouldBlock(url: url, documentURL: documentURL, type: type)
            if blocked { log.record(url: url, documentURL: documentURL, type: type) }
            return blocked
        }
        MosaicPrivacyCEF.frameScriptProvider = { [weak self] frameURL, pageURL, isMainFrame in
            MainActor.assumeIsolated { self?.cosmeticScript(frameURL: frameURL, pageURL: pageURL, isMainFrame: isMainFrame) }
        }
        do {
            try store.seed(from: resources.appendingPathComponent("lists", isDirectory: true))
        } catch {
            status = .failed("The filter lists could not be installed: \(error.localizedDescription)")
            return
        }
        listsDate = store.oldestModification()
        rebuildEngine()

        // Lists turned on but never downloaded (optional ones are not shipped).
        if !store.missingLists().isEmpty {
            Task { await downloadMissingLists() }
        }
        // Refresh stale lists once the browser has settled.
        if listsDate.map({ Date().timeIntervalSince($0) > Self.refreshInterval }) ?? true {
            Task {
                try? await Task.sleep(nanoseconds: 20_000_000_000)
                await updateLists()
            }
        }
    }

    /// Compiles the lists on disk into a new engine and swaps it in. The
    /// previous engine keeps answering until the new one is ready.
    private func rebuildEngine() {
        guard let bundle = Self.engineBundle else { return }
        buildGeneration += 1
        let generation = buildGeneration
        let store = self.store
        if case .ready = status {} else { status = .loading }

        Task.detached(priority: .userInitiated) {
            let result = Result<BlockerEngine, Error> {
                guard let suffixList = store.suffixList() else {
                    throw BlockerEngine.EngineError.script("the Public Suffix List is missing")
                }
                return try BlockerEngine(bundle: bundle, suffixList: suffixList, lists: store.loadLists())
            }
            await MainActor.run { [weak self] in
                guard let self, generation == self.buildGeneration else { return }
                switch result {
                case .success(let engine):
                    self.engine = engine
                    self.cosmeticCache = CosmeticStyleCache(engine: engine)
                    self.cosmeticStats = engine.cosmeticStats
                    self.filter.use(engine: engine)
                    self.engineVersion = engine.version
                    self.status = .ready(filters: engine.filterCount)
                case .failure(let error):
                    self.status = .failed(error.localizedDescription)
                }
            }
        }
    }

    /// Loads the engine into Gecko. Called each time Gecko has started, and
    /// after the lists change.
    func configureGecko() async {
        guard status != .unavailable, gecko.isRunning, let bundle = Self.engineBundle else { return }
        do {
            try await gecko.call(
                "configureBlocker", bundle.path, store.suffixListURL.path, store.availableListPaths(),
                settings.isEnabled, Array(settings.allowlist))
        } catch {
            NSLog("Mosaic: could not load the content blocker into Gecko: %@", error.localizedDescription)
        }
    }

    /// Downloads fresh filter lists and rebuilds both engines from them.
    func updateLists() async {
        guard status != .unavailable, !isUpdating else { return }
        isUpdating = true
        defer { isUpdating = false }
        let result = await store.update()
        lastUpdateMessage = Self.describe(result)
        guard !result.updated.isEmpty else { return }
        listsDate = store.oldestModification()
        await rebuildBothEngines()
    }

    private func downloadMissingLists() async {
        let missing = Set(store.missingLists().map(\.id))
        guard !missing.isEmpty, !isUpdating else { return }
        isUpdating = true
        defer { isUpdating = false }
        let result = await store.update(only: missing)
        lastUpdateMessage = Self.describe(result)
        if !result.updated.isEmpty {
            listsDate = store.oldestModification()
            await rebuildBothEngines()
        }
    }

    private func rebuildBothEngines() async {
        rebuildEngine()
        await configureGecko()
    }

    private static func describe(_ result: FilterListStore.UpdateResult) -> String {
        if result.failed.isEmpty { return "All lists are up to date." }
        let names = result.failed.map { id in FilterListCatalog.all.first { $0.id == id }?.title ?? id }
        return "Could not download: \(names.joined(separator: ", "))."
    }

    // MARK: - Settings

    var isEnabled: Bool { settings.isEnabled }

    func setEnabled(_ enabled: Bool) {
        guard settings.isEnabled != enabled else { return }
        settings.isEnabled = enabled
        settingsChanged()
    }

    /// The site a page belongs to, if the blocker applies to pages like it.
    func site(ofPage url: String) -> String? {
        BlockerSettings.site(ofPage: url)
    }

    /// Whether requests from this page are being filtered right now.
    func isFiltering(pageURL: String) -> Bool {
        status != .unavailable && settings.filters(pageURL: pageURL)
    }

    /// Turns blocking on or off for the site `pageURL` belongs to.
    func setBlocking(_ blocking: Bool, onSiteOf pageURL: String) {
        guard let site = site(ofPage: pageURL) else { return }
        settings.setAllowlisted(!blocking, site: site)
        settingsChanged()
    }

    /// Removes a site from the allowlist (Settings), or adds one.
    func setAllowlisted(_ allowlisted: Bool, site: String) {
        settings.setAllowlisted(allowlisted, site: site)
        settingsChanged()
    }

    private func settingsChanged() {
        let defaults = AppPaths.defaults
        defaults.set(settings.isEnabled, forKey: Keys.enabled)
        defaults.set(settings.allowlist.sorted(), forKey: Keys.allowlist)
        filter.use(settings: settings)
        gecko.send("setBlockerOptions", settings.isEnabled, Array(settings.allowlist))
    }

    func setCosmeticEnabled(_ enabled: Bool) {
        guard cosmeticEnabled != enabled else { return }
        cosmeticEnabled = enabled
        AppPaths.defaults.set(enabled, forKey: Keys.cosmetic)
        gecko.send("setCosmeticOptions", enabled)
    }

    /// Turns a filter list on or off. A list never downloaded is fetched first.
    func setList(_ list: FilterList, enabled: Bool) {
        guard enabledListIDs.contains(list.id) != enabled else { return }
        if enabled { enabledListIDs.insert(list.id) } else { enabledListIDs.remove(list.id) }
        AppPaths.defaults.set(enabledListIDs.sorted(), forKey: Keys.lists)
        store = FilterListStore(directory: AppPaths.blockerLists, lists: FilterListCatalog.lists(enabled: enabledListIDs))
        guard status != .unavailable else { return }
        if enabled, !store.missingLists().isEmpty {
            Task { await downloadMissingLists() }
        } else {
            Task { await rebuildBothEngines() }
        }
    }

    func isListDownloaded(_ list: FilterList) -> Bool {
        FileManager.default.fileExists(atPath: store.fileURL(for: list).path)
    }

    // MARK: - My filters

    /// Checks filters with uBO's parser. Empty until the engine has loaded.
    func validate(filters text: String) -> [FilterCheck] {
        engine?.validate(filters: text) ?? []
    }

    var canValidate: Bool { engine != nil }

    /// Saves the user's filters and rebuilds both engines with them.
    func saveUserFilters(_ text: String) throws {
        try store.saveUserFilters(text)
        userFilters = text
        guard status != .unavailable else { return }
        Task { await rebuildBothEngines() }
    }

    // MARK: - Cosmetic filtering (Chromium)

    /// The style-injection script for a Chromium frame, or nil. The generic
    /// stylesheet (half a megabyte with the default lists) goes to the page
    /// itself and to frames on the page's own host; other frames get only
    /// their site's specific rules, which keeps ad-heavy pages with many
    /// third-party frames fast.
    private func cosmeticScript(frameURL: String, pageURL: String, isMainFrame: Bool) -> String? {
        // A page committing a new document starts its dashboard log over.
        if isMainFrame { chromiumLog.clear(page: frameURL) }
        guard cosmeticEnabled, let cache = cosmeticCache, settings.filters(pageURL: pageURL),
              let host = URLComponents(string: frameURL)?.host, !host.isEmpty
        else { return nil }
        let pageHost = URLComponents(string: pageURL)?.host
        let css = cache.stylesheet(forHostname: host, includeGeneric: isMainFrame || pageHost == host)
        return CosmeticInjection.script(css: css)
    }

    // MARK: - Dashboard

    /// Requests blocked on the tab's current page, with the filter behind each.
    func blockedRequests(in tab: BrowserTab) async -> [BlockedRequest] {
        var found: [(url: String, type: String, count: Int, filter: String)] = []
        switch tab.engine {
        case .chromium:
            let entries = chromiumLog.entries(forPage: tab.url)
            let engine = self.engine
            found = await Task.detached {
                entries.map { entry in
                    (entry.url, entry.type, entry.count,
                     engine?.details(url: entry.url, documentURL: entry.documentURL, type: entry.type).filter ?? "")
                }
            }.value
        case .gecko:
            guard let id = tab.geckoID,
                  let records = try? await gecko.call("blockedRequests", id) as? [[String: Any]]
            else { break }
            found = records.map {
                ($0["url"] as? String ?? "", $0["type"] as? String ?? "", ($0["count"] as? Int) ?? 1,
                 $0["filter"] as? String ?? "")
            }
        }
        let store = self.store
        return await Task.detached {
            var listByFilter: [String: String?] = [:]
            return found.map { item in
                if listByFilter[item.filter] == nil { listByFilter[item.filter] = store.listContaining(filter: item.filter) }
                return BlockedRequest(url: item.url, type: item.type, count: item.count, filter: item.filter,
                                      list: listByFilter[item.filter] ?? nil)
            }
        }.value
    }
}
