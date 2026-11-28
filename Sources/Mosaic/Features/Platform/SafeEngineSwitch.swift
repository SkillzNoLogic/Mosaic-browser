import AppKit
import CEFBridge
import MosaicCore

/// Tabs being moved to the other engine. While a tab is listed it still shows its page in
/// the old engine; the other engine is loading the same address out of sight.
@MainActor
final class EngineSwitchTracker: ObservableObject {
    struct Pending {
        let target: EngineKind
        let url: String
        let started: Date
        let task: Task<Void, Never>
    }

    @Published private(set) var pending: [UUID: Pending] = [:]
    /// What happened to the most recent switch, for automation and diagnostics.
    @Published private(set) var lastOutcome: String?

    func target(of tab: BrowserTab) -> EngineKind? { pending[tab.id]?.target }

    fileprivate func begin(_ tab: BrowserTab, _ pending: Pending) {
        self.pending[tab.id]?.task.cancel()
        self.pending[tab.id] = pending
        lastOutcome = "switching to \(pending.target.rawValue)"
    }

    fileprivate func end(_ tab: BrowserTab, outcome: String) {
        pending[tab.id] = nil
        lastOutcome = outcome
    }

    fileprivate func cancel(_ tab: BrowserTab) {
        guard let entry = pending.removeValue(forKey: tab.id) else { return }
        entry.task.cancel()
        lastOutcome = "cancelled"
    }
}

/// Safe engine switching: moving a tab to the other engine keeps the original page on
/// screen until the other engine has loaded it. If the other engine fails (network error,
/// certificate error, not installed, too slow), the tab stays where it was and a notice
/// says why.
///
/// Chromium → Mozilla: Gecko opens the address in a background tab that Mosaic does not
/// mirror; the Chromium view stays until `pageStatus` reports the page loaded.
/// Mozilla → Chromium: a Chromium view loads the address outside the window's visible area;
/// the Gecko page stays until that view reports the load finished.
extension BrowserModel {
    /// Hook from `setEngine`. Returns false when there is nothing to keep on screen (a blank
    /// tab, Mosaic's own page, a failed page, or a page not loaded yet): the caller then
    /// switches directly.
    func beginSafeEngineSwitch(to kind: EngineKind) -> Bool {
        guard let tab = selectedTab else { return false }
        if tab.engine == kind {
            cancelEngineSwitch(of: tab)
            return false
        }
        if platform.switches.target(of: tab) == kind { return true }
        let isLive = switch tab.engine {
        case .chromium: tab.chromiumView != nil
        case .gecko: tab.geckoID != nil && gecko.isRunning
        }
        guard isLive, tab.internalPage == nil, !tab.isStartPage, tab.failure == nil,
              let url = URL(string: tab.url), url.scheme != nil, tab.url != "about:blank"
        else { return false }

        let address = tab.url
        let original = tab.engine
        let loadRequest = tab.lastLoadRequest
        notice = "Opening this page in \(kind.displayName)…"
        let task = Task { [weak self, weak tab] in
            guard let self, let tab else { return }
            let result: SwitchResult
            switch kind {
            case .gecko: result = await self.switchToGecko(tab, address: address, from: original, since: loadRequest)
            case .chromium: result = await self.switchToChromium(tab, address: address, from: original, since: loadRequest)
            }
            self.finishSwitch(tab, to: kind, from: original, result: result)
        }
        platform.switches.begin(tab, .init(target: kind, url: address, started: Date(), task: task))
        // The engine for new tabs follows the explicit choice, as before.
        platformFollowEngine(kind, remember: true)
        return true
    }

    func cancelEngineSwitch(of tab: BrowserTab) {
        platform.switches.cancel(tab)
    }

    fileprivate enum SwitchResult {
        case committed
        case failed(String)
        /// The tab was closed, navigated or switched again meanwhile.
        case abandoned
    }

    private static let switchTimeout: TimeInterval = 30
    /// A page that has shown its document but keeps loading (slow trackers, streaming) is
    /// taken over after this long rather than waiting for the last request.
    private static let commitGrace: TimeInterval = 6

    /// The switch still makes sense: the tab exists, is in the engine it started in, and has
    /// not navigated elsewhere (a new load, or a different document) in the meantime.
    private func switchStillWanted(_ tab: BrowserTab, address: String, from original: EngineKind, since: Date) -> Bool {
        guard !Task.isCancelled, tabs.contains(tab), tab.engine == original, tab.lastLoadRequest == since
        else { return false }
        return Self.sameDocument(tab.url, address)
    }

    nonisolated static func sameDocument(_ lhs: String, _ rhs: String) -> Bool {
        func strip(_ text: String) -> String { text.split(separator: "#", maxSplits: 1).first.map(String.init) ?? text }
        return strip(lhs) == strip(rhs)
    }

    // MARK: Chromium → Gecko

    private func switchToGecko(_ tab: BrowserTab, address: String, from original: EngineKind, since: Date) async -> SwitchResult {
        guard !geckoRuntimeMissing else {
            return .failed("The Mozilla engine is not installed. Install it in Settings › Engine.")
        }
        var geckoTab: Int?
        do {
            try await gecko.start()
            guard switchStillWanted(tab, address: address, from: original, since: since) else { return .abandoned }
            geckoTab = try await platformOpeningGeckoTab {
                let id = try await gecko.call("openTab", address, false) as? Int
                if let id { platformHideGeckoTab(id, true) }
                return id
            }
            guard let id = geckoTab else { return .failed("Mozilla did not open a tab.") }

            let started = Date()
            var committedAt: Date?
            while true {
                try await Task.sleep(nanoseconds: 200_000_000)
                guard switchStillWanted(tab, address: address, from: original, since: since) else {
                    gecko.send("closeTab", id)
                    return .abandoned
                }
                let status = try await gecko.call("pageStatus", id) as? [String: Any] ?? [:]
                if let error = status["error"] as? String {
                    gecko.send("closeTab", id)
                    return .failed("Mozilla could not load the page (\(error)).")
                }
                let document = status["document"] as? String ?? ""
                let loading = status["loading"] as? Bool ?? true
                if !document.isEmpty, document != "about:blank" {
                    committedAt = committedAt ?? Date()
                    if !loading || Date().timeIntervalSince(committedAt!) > Self.commitGrace {
                        return await commitToGecko(tab, geckoID: id, loading: loading,
                                                   title: status["title"] as? String ?? "")
                    }
                }
                if Date().timeIntervalSince(started) > Self.switchTimeout {
                    gecko.send("closeTab", id)
                    return .failed("Mozilla took longer than \(Int(Self.switchTimeout)) seconds to load the page.")
                }
            }
        } catch is CancellationError {
            if let geckoTab { gecko.send("closeTab", geckoTab) }
            return .abandoned
        } catch {
            if let geckoTab { gecko.send("closeTab", geckoTab) }
            return .failed(error.localizedDescription)
        }
    }

    private func commitToGecko(_ tab: BrowserTab, geckoID id: Int, loading: Bool, title: String) async -> SwitchResult {
        if tab === selectedTab { _ = try? await gecko.call("selectTab", id) }
        guard tabs.contains(tab), tab.engine == .chromium else {
            gecko.send("closeTab", id)
            return .abandoned
        }
        let oldView = tab.chromiumView
        platformHideGeckoTab(id, false)
        tab.chromiumView = nil
        tab.engine = .gecko
        tab.geckoID = id
        tab.isLoading = loading
        tab.canGoBack = false
        tab.canGoForward = false
        tab.blockedCount = 0
        if !title.isEmpty { tab.title = title }
        // Gecko's window is shown first; the Chromium view goes a moment later, so the
        // content area never shows through to the desktop.
        platformRefreshDocking()
        objectWillChange.send()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { oldView?.closeBrowser() }
        return .committed
    }

    // MARK: Gecko → Chromium

    private func switchToChromium(_ tab: BrowserTab, address: String, from original: EngineKind, since: Date) async -> SwitchResult {
        guard chromiumAvailable, let host = Self.stagingHost() else {
            return .failed("The Chromium engine is not available in this build.")
        }
        let probe = ChromiumLoadProbe()
        let view = MCEFBrowserView(url: address)
        view.delegate = probe
        // Outside the window's visible area, but inside a window, so Chromium creates and
        // loads the browser normally.
        let size = host.bounds.size
        view.frame = NSRect(x: size.width + 64, y: 0, width: max(size.width, 400), height: max(size.height, 300))
        host.addSubview(view)

        let started = Date()
        do {
            while true {
                try await Task.sleep(nanoseconds: 150_000_000)
                guard switchStillWanted(tab, address: address, from: original, since: since) else {
                    view.closeBrowser()
                    return .abandoned
                }
                if let error = probe.error {
                    view.closeBrowser()
                    return .failed("Chromium could not load the page (\(error)).")
                }
                if let committed = probe.committedAt,
                   probe.finished || Date().timeIntervalSince(committed) > Self.commitGrace
                {
                    return commitToChromium(tab, view: view, probe: probe)
                }
                if Date().timeIntervalSince(started) > Self.switchTimeout {
                    view.closeBrowser()
                    return .failed("Chromium took longer than \(Int(Self.switchTimeout)) seconds to load the page.")
                }
            }
        } catch {
            view.closeBrowser()
            return .abandoned
        }
    }

    private func commitToChromium(_ tab: BrowserTab, view: MCEFBrowserView, probe: ChromiumLoadProbe) -> SwitchResult {
        let geckoID = tab.geckoID
        tab.geckoWork?.cancel()
        tab.geckoWork = nil
        tab.geckoID = nil
        if let geckoID { platformHideGeckoTab(geckoID, true) }
        tab.engine = .chromium
        tab.chromiumView = view
        view.delegate = tab
        if let url = probe.url { tab.url = url }
        if let title = probe.title { tab.title = title }
        tab.isLoading = !probe.finished
        tab.canGoBack = probe.canGoBack
        tab.canGoForward = probe.canGoForward
        tab.blockedCount = probe.blocked
        objectWillChange.send()
        // The content area now draws the Chromium view (opaque) in front of Gecko's
        // window; hide Gecko and close its tab once that has happened.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
            MainActor.assumeIsolated {
                self?.platformRefreshDocking()
                if let geckoID { self?.gecko.send("closeTab", geckoID) }
            }
        }
        return .committed
    }

    /// The view staging Chromium pages are parked in: the main window's content view.
    private static func stagingHost() -> NSView? {
        let windows = NSApp.windows.filter { $0.isVisible && !($0 is NSPanel) && $0.contentView != nil }
        let window = windows.first { $0.frameAutosaveName == "MosaicMainWindow" }
            ?? NSApp.mainWindow ?? windows.first
        return window?.contentView
    }

    // MARK: Outcome

    private func finishSwitch(_ tab: BrowserTab, to kind: EngineKind, from original: EngineKind, result: SwitchResult) {
        switch result {
        case .committed:
            platform.switches.end(tab, outcome: "committed to \(kind.rawValue)")
            if notice?.hasPrefix("Opening this page in") == true { notice = nil }
            tabChangedForPlatform(tab)
        case .failed(let reason):
            platform.switches.end(tab, outcome: "failed: \(reason)")
            notice = "\(reason) The page stays in \(original.displayName)."
            // The engine for new tabs goes back to the one the tab is still in.
            if tab === selectedTab { platformFollowEngine(original, remember: true) }
        case .abandoned:
            if platform.switches.target(of: tab) == kind { platform.switches.end(tab, outcome: "abandoned") }
            if notice?.hasPrefix("Opening this page in") == true { notice = nil }
        }
    }

    private func tabChangedForPlatform(_ tab: BrowserTab) {
        if tab === selectedTab, !isEditingAddress { addressText = tab.url }
        objectWillChange.send()
    }
}

/// Watches a Chromium view that is loading out of sight.
@MainActor
final class ChromiumLoadProbe: NSObject, MCEFBrowserDelegate {
    private(set) var url: String?
    private(set) var title: String?
    private(set) var canGoBack = false
    private(set) var canGoForward = false
    private(set) var blocked = 0
    private(set) var error: String?
    /// When the page's own address was first reported (its document is committing).
    private(set) var committedAt: Date?
    private(set) var finished = false
    private var startedLoading = false

    nonisolated func browserView(_ view: MCEFBrowserView, didChangeTitle title: String) {
        MainActor.assumeIsolated { self.title = title }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, didChangeURL url: String) {
        MainActor.assumeIsolated {
            self.url = url
            if url != "about:blank", committedAt == nil { committedAt = Date() }
        }
    }

    nonisolated func browserView(
        _ view: MCEFBrowserView, didChangeLoading isLoading: Bool, canGoBack: Bool, canGoForward: Bool
    ) {
        MainActor.assumeIsolated {
            self.canGoBack = canGoBack
            self.canGoForward = canGoForward
            if isLoading {
                startedLoading = true
                finished = false
            } else if startedLoading, error == nil {
                finished = true
                if committedAt == nil { committedAt = Date() }
            }
        }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, wantsNewTabWithURL url: String) {}

    nonisolated func browserViewDidStartDocumentLoad(_ view: MCEFBrowserView) {}

    nonisolated func browserView(_ view: MCEFBrowserView, didBlockRequest url: String) {
        MainActor.assumeIsolated { blocked += 1 }
    }

    nonisolated func browserView(
        _ view: MCEFBrowserView, didFailLoadWithErrorCode errorCode: Int, errorText: String, url: String
    ) {
        MainActor.assumeIsolated {
            error = errorText.isEmpty ? "error \(errorCode)" : "\(errorText), \(errorCode)"
        }
    }
}
