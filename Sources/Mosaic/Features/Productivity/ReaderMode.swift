import AppKit
import CEFBridge
import Combine
import MosaicCore

/// Reader view in both engines.
///
/// - **Gecko** has its own reader view (`about:reader?url=…`, with Firefox's toolbar for
///   font, size, width, spacing and colours). Mosaic turns it on and off through the agent,
///   which uses Firefox's own reader actor, and keeps Firefox's `reader.*` preferences in
///   step with `settings` (both directions).
/// - **Chromium** has none, so Mosaic injects Mozilla's Readability.js (vendored in
///   `Resources/reader/`) to extract the article and draws a reader page over the tab
///   (`reader-view.js`), with the same controls and the same scales. Leaving removes it;
///   the page underneath is untouched.
///
/// Each tab exposes `isReaderAvailable` (the page looks like an article) and
/// `isReaderActive` for the shell's indicator.
@MainActor
final class ReaderMode: ObservableObject {
    @Published private(set) var settings: ReaderSettings

    private unowned let model: BrowserModel
    private var tabsWatcher: AnyCancellable?
    private var tabWatchers: [UUID: AnyCancellable] = [:]
    private var lastURL: [UUID: String] = [:]
    private var lastEngine: [UUID: EngineKind] = [:]
    private var probes: [UUID: Task<Void, Never>] = [:]
    /// Chromium tabs showing the reader overlay, and the page it was made from.
    private var chromiumReaders: [UUID: String] = [:]

    private static let defaultsKey = "productivity.reader"
    private static let messageName = "__mosaicReaderPost"
    private static let geckoReaderPrefix = "about:reader?"

    init(model: BrowserModel) {
        self.model = model
        settings = AppPaths.defaults.data(forKey: Self.defaultsKey)
            .flatMap { try? JSONDecoder().decode(ReaderSettings.self, from: $0) } ?? ReaderSettings()
    }

    func start() {
        tabsWatcher = model.$tabs.sink { [weak self] tabs in
            DispatchQueue.main.async { self?.watch(tabs) }
        }
    }

    // MARK: Settings

    /// New settings from Mosaic's side (palette, automation): saved, and applied to every
    /// open reader view.
    func update(_ new: ReaderSettings) {
        store(new)
        for tab in model.tabs where tab.isReaderActive {
            switch tab.engine {
            case .chromium:
                tab.chromiumView?.evaluatePageScript("window.__mosaicReader && window.__mosaicReader.apply(\(settingsJSON))") { _, _ in }
            case .gecko:
                guard let id = tab.geckoID else { continue }
                // Gecko's reader reads its preferences when the page loads.
                Task {
                    _ = try? await model.gecko.call("setReaderPrefs", settings.geckoPreferences)
                    model.gecko.send("reload", id)
                }
            }
        }
    }

    private func store(_ new: ReaderSettings) {
        guard new != settings else { return }
        settings = new
        if let data = try? JSONEncoder().encode(new) { AppPaths.defaults.set(data, forKey: Self.defaultsKey) }
    }

    private var settingsJSON: String {
        (try? JSONEncoder().encode(settings)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
    }

    // MARK: Turning it on and off

    func toggle(_ tab: BrowserTab) {
        setActive(!tab.isReaderActive, for: tab)
    }

    func setActive(_ on: Bool, for tab: BrowserTab) {
        guard tab.isReaderActive != on else { return }
        guard !tab.isStartPage, tab.internalPage == nil, tab.failure == nil else {
            if on { model.notice = "Reader view is for web pages." }
            return
        }
        switch tab.engine {
        case .chromium: on ? enterChromium(tab) : leaveChromium(tab)
        case .gecko: setGecko(on, tab)
        }
    }

    /// Whether the menu item can act on the selected tab, with its check mark.
    func validate(_ item: NSMenuItem) -> Bool {
        let tab = model.selectedTab
        item.state = tab?.isReaderActive == true ? .on : .off
        guard let tab, !tab.isStartPage, tab.internalPage == nil, tab.failure == nil else { return false }
        return tab.engine == .gecko ? tab.geckoID != nil : tab.chromiumView?.hasPageScriptSupport == true
    }

    // MARK: Chromium

    private struct Scripts {
        let probe: String
        let bootstrap: String
    }

    /// Readability, its "readerable" check and the reader page, each wrapped in a function
    /// so nothing leaks into the page's globals.
    private lazy var scripts: Scripts? = {
        guard let folder = Bundle.main.resourceURL?.appendingPathComponent("reader", isDirectory: true),
              let readability = try? String(contentsOf: folder.appendingPathComponent("Readability.js"), encoding: .utf8),
              let readerable = try? String(contentsOf: folder.appendingPathComponent("Readability-readerable.js"), encoding: .utf8),
              let view = try? String(contentsOf: folder.appendingPathComponent("reader-view.js"), encoding: .utf8)
        else { return nil }
        return Scripts(
            probe: "(function () { var module;\n\(readerable)\nreturn isProbablyReaderable(document); })()",
            bootstrap: "(function () { var module;\n\(readability)\n\(view)\n})();")
    }()

    private func enterChromium(_ tab: BrowserTab) {
        guard let view = tab.chromiumView, view.hasPageScriptSupport else {
            model.notice = "The page is still loading."
            return
        }
        guard let scripts else {
            model.notice = "Reader view is missing from this build (Resources/reader)."
            return
        }
        let url = tab.url
        view.addPageMessageHandlerNamed(Self.messageName) { [weak self, weak tab] payload in
            MainActor.assumeIsolated {
                guard let self, let tab else { return }
                self.handleMessage(payload, from: tab)
            }
        }
        view.evaluatePageScript(scripts.bootstrap + "\nwindow.__mosaicReader.enter(\(settingsJSON));") { [weak self, weak tab] value, error in
            MainActor.assumeIsolated {
                guard let self, let tab, tab.chromiumView === view else { return }
                if let result = value as? [String: Any], result["ok"] as? Bool == true {
                    self.chromiumReaders[tab.id] = url
                    tab.isReaderActive = true
                    tab.isReaderAvailable = true
                } else {
                    view.removePageMessageHandlerNamed(Self.messageName)
                    self.model.notice = error == nil
                        ? "Reader view isn’t available for this page."
                        : "Reader view could not read this page."
                }
            }
        }
    }

    private func leaveChromium(_ tab: BrowserTab) {
        chromiumReaders[tab.id] = nil
        tab.isReaderActive = false
        guard let view = tab.chromiumView else { return }
        view.evaluatePageScript("window.__mosaicReader ? window.__mosaicReader.exit() : false") { _, _ in }
        view.removePageMessageHandlerNamed(Self.messageName)
    }

    /// Messages from the reader page: its toolbar changed a setting, or it was closed.
    private func handleMessage(_ payload: String, from tab: BrowserTab) {
        guard let object = (try? JSONSerialization.jsonObject(with: Data(payload.utf8))) as? [String: Any],
              let type = object["type"] as? String else { return }
        switch type {
        case "settings":
            guard let raw = object["settings"],
                  let data = try? JSONSerialization.data(withJSONObject: raw),
                  let new = try? JSONDecoder().decode(ReaderSettings.self, from: data) else { return }
            store(new.clamped)
            // Other open reader views follow.
            for other in model.tabs where other !== tab && other.isReaderActive && other.engine == .chromium {
                other.chromiumView?.evaluatePageScript("window.__mosaicReader && window.__mosaicReader.apply(\(settingsJSON))") { _, _ in }
            }
        case "closed":
            chromiumReaders[tab.id] = nil
            tab.isReaderActive = false
            tab.chromiumView?.removePageMessageHandlerNamed(Self.messageName)
        default:
            break
        }
    }

    // MARK: Gecko

    private func setGecko(_ on: Bool, _ tab: BrowserTab) {
        guard let id = tab.geckoID, model.gecko.isRunning else {
            model.notice = "The page is still loading."
            return
        }
        Task {
            do {
                if on {
                    try await model.gecko.call("setReaderPrefs", settings.geckoPreferences)
                } else {
                    await pullGeckoPrefs()
                }
                try await model.gecko.call("setReaderMode", id, on)
            } catch {
                model.notice = error.localizedDescription
            }
        }
    }

    /// Takes over what the user changed in Gecko's own reader toolbar.
    private func pullGeckoPrefs() async {
        guard let prefs = try? await model.gecko.call("readerPrefs") as? [String: Any] else { return }
        store(settings.merging(geckoPreferences: prefs))
    }

    // MARK: Watching tabs

    private func watch(_ tabs: [BrowserTab]) {
        let ids = Set(tabs.map(\.id))
        for id in Array(tabWatchers.keys) where !ids.contains(id) {
            tabWatchers[id] = nil
            probes.removeValue(forKey: id)?.cancel()
            lastURL[id] = nil
            lastEngine[id] = nil
            chromiumReaders[id] = nil
        }
        for tab in tabs where tabWatchers[tab.id] == nil {
            // @Published fires before the value is stored: look at the tab a moment later.
            tabWatchers[tab.id] = tab.$url.combineLatest(tab.$isLoading, tab.$engine, tab.$geckoID)
                .sink { [weak self, weak tab] _ in
                    DispatchQueue.main.async {
                        guard let self, let tab else { return }
                        self.pageChanged(tab)
                    }
                }
        }
    }

    private func pageChanged(_ tab: BrowserTab) {
        let url = tab.url
        let urlChanged = lastURL[tab.id] != url
        let engineChanged = lastEngine[tab.id] != nil && lastEngine[tab.id] != tab.engine
        lastURL[tab.id] = url
        lastEngine[tab.id] = tab.engine

        // A tab moved to Chromium while showing Gecko's reader view: Chromium cannot load
        // about:reader, so open the article itself there.
        if tab.engine == .chromium, url.hasPrefix(Self.geckoReaderPrefix),
           let original = URLComponents(string: url)?.queryItems?.first(where: { $0.name == "url" })?.value {
            tab.isReaderActive = false
            tab.isReaderAvailable = false
            model.load(original, in: tab)
            return
        }

        switch tab.engine {
        case .chromium:
            if engineChanged || (chromiumReaders[tab.id] != nil && (chromiumReaders[tab.id] != url || tab.isLoading)) {
                // A new document replaced the one the reader overlay was drawn on.
                chromiumReaders[tab.id] = nil
                tab.chromiumView?.removePageMessageHandlerNamed(Self.messageName)
                if tab.isReaderActive { tab.isReaderActive = false }
            }
        case .gecko:
            let active = url.hasPrefix(Self.geckoReaderPrefix)
            if tab.isReaderActive, !active { Task { await pullGeckoPrefs() } }
            if tab.isReaderActive != active { tab.isReaderActive = active }
        }

        if urlChanged || engineChanged {
            probes.removeValue(forKey: tab.id)?.cancel()
            let available = tab.engine == .gecko && tab.isReaderActive
            if tab.isReaderAvailable != available { tab.isReaderAvailable = available }
        }
        if !tab.isLoading, probes[tab.id] == nil, !tab.isReaderAvailable, Self.isWebPage(url) {
            probes[tab.id] = Task { [weak self, weak tab] in
                guard let self, let tab else { return }
                await self.probe(tab, url: url)
            }
        }
    }

    private static func isWebPage(_ url: String) -> Bool {
        url.hasPrefix("http://") || url.hasPrefix("https://")
    }

    private func probeChromium(_ view: MCEFBrowserView, script: String, tab: BrowserTab, url: String) {
        view.evaluatePageScript(script) { [weak tab] value, _ in
            MainActor.assumeIsolated {
                guard let tab, tab.url == url, let available = value as? Bool else { return }
                if tab.isReaderAvailable != available { tab.isReaderAvailable = available }
            }
        }
    }

    /// Asks the engine whether the page looks like an article.
    private func probe(_ tab: BrowserTab, url: String) async {
        switch tab.engine {
        case .chromium:
            try? await Task.sleep(nanoseconds: 150_000_000)
            guard let view = tab.chromiumView, view.hasPageScriptSupport, let scripts, tab.url == url else {
                // Not ready yet: let the next change (load finished) try again.
                probes[tab.id] = nil
                return
            }
            probeChromium(view, script: scripts.probe, tab: tab, url: url)
        case .gecko:
            // Gecko judges the page shortly after it loads; ask a few times.
            for delay in [0.3, 1.0, 2.5] {
                try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
                guard !Task.isCancelled, tab.url == url, let id = tab.geckoID,
                      let info = try? await model.gecko.call("readerInfo", id) as? [String: Any] else { return }
                if info["available"] as? Bool == true {
                    if tab.url == url { tab.isReaderAvailable = true }
                    return
                }
            }
        }
    }

    /// Before the browsers close at quit: no page-script channel may outlive its browser.
    func prepareToQuit() {
        for tab in model.tabs {
            tab.chromiumView?.removePageMessageHandlerNamed(Self.messageName)
        }
        chromiumReaders.removeAll()
    }

    // MARK: Automation

    var automationState: [String: Any] {
        [
            "settings": (try? JSONSerialization.jsonObject(with: JSONEncoder().encode(settings))) ?? [:],
            "tabs": model.tabs.map { tab in
                ["available": tab.isReaderAvailable, "active": tab.isReaderActive,
                 "engine": tab.engine.rawValue, "url": tab.url] as [String: Any]
            },
        ]
    }
}
