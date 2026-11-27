import AppKit
import CEFBridge
import MosaicCore

/// Platform-area behaviour of the browser model: per-site engine rules, the default
/// engine, the Gecko runtime's availability, and extension toolbar buttons. Safe engine
/// switching is in `SafeEngineSwitch.swift`. The hooks into `BrowserModel.swift` are one
/// line each (marked "platform").
extension BrowserModel {
    // MARK: - Engines

    /// True when no Gecko runtime can be found at all: Mozilla tabs cannot load, and the
    /// UI offers to install one (`GeckoRuntimeMissingView`, Settings › Engine).
    var geckoRuntimeMissing: Bool { gecko.runtime == nil }

    /// Whether a tab could load in `kind` right now (or after starting the engine).
    func isEngineUsable(_ kind: EngineKind) -> Bool {
        switch kind {
        case .chromium: chromiumAvailable
        case .gecko: !geckoRuntimeMissing
        }
    }

    /// The engine Mosaic starts with and new tabs use (until a tab of the other engine is
    /// selected: new tabs open next to their neighbours' engine). Persisted.
    var defaultEngine: EngineKind {
        AppPaths.defaults.string(forKey: "engine").flatMap(EngineKind.init(rawValue:)) ?? engine
    }

    func setDefaultEngine(_ kind: EngineKind) {
        guard isEngineUsable(kind) || kind == .gecko else { return }
        platformFollowEngine(kind, remember: true)
        // A blank new tab has no engine attached yet; let it follow the choice.
        if let tab = selectedTab, tab.isStartPage, tab.chromiumView == nil, tab.geckoID == nil {
            tab.engine = kind
            objectWillChange.send()
        }
    }

    // MARK: - Per-site engine rules
    //
    // Rules apply when a page is opened from the address bar, the start page, or as a new
    // tab with an address (bookmarks, history, links opened by Mosaic itself). They do not
    // apply to navigations inside a page (following a link, redirects) or to tabs a page
    // opens: moving a page between engines mid-visit would reload it and lose its state.

    /// Engine for a new tab opened at `url`, when a rule covers it.
    func engineRule(forNewTabURL url: String) -> EngineKind? {
        guard !url.isEmpty, let parsed = URL(string: url), let kind = platform.rules.engine(for: parsed),
              isEngineUsable(kind)
        else { return nil }
        return kind
    }

    /// Moves `tab` to the engine a rule names for `url`, before `url` is loaded in it.
    func applyEngineRule(for url: URL, to tab: BrowserTab) {
        guard let rule = platform.rules.rule(for: url), let kind = EngineKind(rawValue: rule.engine),
              kind != tab.engine, isEngineUsable(kind)
        else { return }
        cancelEngineSwitch(of: tab)
        platformReleaseEngine(of: tab)
        tab.engine = kind
        tab.canGoBack = false
        tab.canGoForward = false
        tab.blockedCount = 0
        tab.failure = nil
        if tab === selectedTab { platformFollowEngine(kind, remember: false) }
        notice = "Opening in \(kind.displayName): site rule for \(rule.domain)"
    }

    /// The site of the selected tab, as a rule would store it ("example.com").
    var currentSiteDomain: String? {
        guard let tab = selectedTab, tab.internalPage == nil, let url = URL(string: tab.url),
              ["http", "https"].contains(url.scheme?.lowercased() ?? ""), let host = url.host
        else { return nil }
        return EngineRuleSet.normalizedHost(host)
    }

    /// "Always open this site in …", from the current tab. Moves the tab too.
    func addEngineRuleForCurrentSite(_ kind: EngineKind) {
        guard let domain = currentSiteDomain, platform.rules.set(domain: domain, engine: kind) else { return }
        notice = "\(domain) will always open in \(kind.displayName)"
        if selectedTab?.engine != kind { setEngine(kind) }
    }

    // MARK: - Extension toolbar buttons

    /// Buttons for the toolbar: one per enabled extension, in the order Settings › Extensions lists them. The shell
    /// places them (see `ExtensionToolbarButtons`).
    var extensionToolbarItems: [ExtensionToolbarItem] {
        extensions.extensions.filter(\.enabled).map { ext in
            ExtensionToolbarItem(
                id: ext.id, name: ext.name, icon: extensions.icon(for: ext),
                hasPopup: ext.hasPopup, hasOptions: ext.hasOptions,
                isRunning: extensions.loaded.contains(ext.geckoID)
            ) { [weak self] in
                guard let self else { return }
                if ext.hasPopup {
                    self.openExtensionPage(ext, kind: "popup")
                } else if ext.hasOptions {
                    self.openExtensionPage(ext, kind: "options")
                } else {
                    self.openSettings(.extensions)
                }
            }
        }
    }
}

/// One extension's toolbar button: what the shell draws and what a click does.
struct ExtensionToolbarItem: Identifiable {
    let id: String
    let name: String
    let icon: NSImage?
    let hasPopup: Bool
    let hasOptions: Bool
    /// Loaded in the running Mozilla engine.
    let isRunning: Bool
    /// Opens the extension's popup (as a Mozilla tab), or its options, or Settings › Extensions.
    let perform: () -> Void

    var help: String {
        if hasPopup { return "\(name) (opens in a Mozilla tab)" }
        if hasOptions { return "\(name) options" }
        return name
    }
}
