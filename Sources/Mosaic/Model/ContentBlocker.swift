import AppKit
import CEFBridge
import MosaicCore

/// Mosaic's built-in ad and tracker blocker.
///
/// It is part of the browser, not an extension: uBlock Origin's network
/// filtering engine, built from uBO's source, with uBO's default filter lists.
/// The same engine and lists serve both rendering engines:
///
/// - Chromium tabs: Chromium asks `RequestFilter` (an engine hosted in this
///   process) before each request.
/// - Mozilla tabs: the Gecko agent loads the engine inside Gecko and cancels
///   blocked requests there (`configureBlocker` in `agent.sys.mjs`).
///
/// What it does not do: cosmetic filtering (hiding leftover page elements) and
/// scriptlet injection, which in uBO live in its content scripts.
@MainActor
final class ContentBlocker: ObservableObject {
    enum Status: Equatable {
        /// The engine was not bundled into this build.
        case unavailable
        case loading
        case ready(filters: Int)
        case failed(String)
    }

    @Published private(set) var status: Status = .loading
    @Published private(set) var settings: BlockerSettings
    @Published private(set) var isUpdating = false
    /// When the filter lists were last refreshed.
    @Published private(set) var listsDate: Date?
    /// Version of uBlock Origin the engine was built from.
    @Published private(set) var engineVersion: String?

    private let filter = RequestFilter()
    private let store = FilterListStore(directory: AppPaths.blockerLists)
    private unowned let gecko: GeckoEngine
    private var buildGeneration = 0

    /// Lists older than this are refreshed in the background.
    private static let refreshInterval: TimeInterval = 2 * 24 * 60 * 60

    private enum Keys {
        static let enabled = "blockerEnabled"
        static let allowlist = "blockerAllowlist"
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
        MosaicCEF.requestFilter = { url, documentURL, type in
            filter.shouldBlock(url: url, documentURL: documentURL, type: type)
        }
        do {
            try store.seed(from: resources.appendingPathComponent("lists", isDirectory: true))
        } catch {
            status = .failed("The filter lists could not be installed: \(error.localizedDescription)")
            return
        }
        listsDate = store.oldestModification()
        rebuildEngine()

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
        guard !result.updated.isEmpty else { return }
        listsDate = store.oldestModification()
        rebuildEngine()
        await configureGecko()
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

    private func settingsChanged() {
        let defaults = AppPaths.defaults
        defaults.set(settings.isEnabled, forKey: Keys.enabled)
        defaults.set(settings.allowlist.sorted(), forKey: Keys.allowlist)
        filter.use(settings: settings)
        gecko.send("setBlockerOptions", settings.isEnabled, Array(settings.allowlist))
    }
}
