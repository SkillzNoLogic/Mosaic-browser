import AppKit
import CEFBridge
import MosaicCore

/// The rendering engines a tab can run on.
enum EngineKind: String, CaseIterable, Identifiable {
    case gecko
    case chromium

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .gecko: "Mozilla"
        case .chromium: "Chromium"
        }
    }

    var engineName: String {
        switch self {
        case .gecko: "Gecko"
        case .chromium: "Blink"
        }
    }

    var other: EngineKind { self == .gecko ? .chromium : .gecko }
}

/// One tab. A tab is bound to a single engine at a time and can be moved to the
/// other one, which reloads its page there.
@MainActor
final class BrowserTab: NSObject, ObservableObject, Identifiable {
    nonisolated let id = UUID()

    @Published var engine: EngineKind
    /// Empty for a tab still showing Mosaic's start page.
    @Published var url: String
    @Published var title: String
    @Published var isLoading = false
    @Published var canGoBack = false
    @Published var canGoForward = false
    @Published var failure: String?
    /// Requests the content blocker stopped since this tab's page began loading.
    @Published var blockedCount = 0

    // Organisation (behaviour owned by the tabs area; mute is applied by the engine area).
    @Published var isPinned = false
    @Published var isMuted = false
    @Published var workspaceID: UUID?
    @Published var groupID: UUID?

    // Engine area: the page's zoom (1 = 100 %), remembered per site (Features/Engine/PageFeatures.swift).
    @Published var zoomFactor: Double = 1
    /// Set by engines that can tell. (Also declared by the tabs area on `next`: keep one.)
    @Published var isPlayingAudio = false

    /// Chromium: the view hosting this tab's browser.
    var chromiumView: MCEFBrowserView?
    /// Gecko: the agent's ID for this tab, once Gecko has created it.
    @Published var geckoID: Int?
    /// Gecko: serialises open/navigate so a tab is never created twice.
    var geckoWork: Task<Void, Never>?
    var lastLoadRequest = Date.distantPast

    /// Called when anything the UI shows about this tab changed.
    var onChange: ((BrowserTab) -> Void)?
    var onNewTabRequest: ((BrowserTab, String) -> Void)?

    init(engine: EngineKind, url: String = "", title: String = "") {
        self.engine = engine
        self.url = url
        self.title = title
    }

    var isStartPage: Bool { url.isEmpty }

    /// Set when the tab shows one of Mosaic's own pages instead of a web page.
    var internalPage: InternalPage? { InternalPage(url: url) }

    var displayTitle: String {
        if isStartPage { return "New Tab" }
        if !title.isEmpty { return title }
        return URL(string: url)?.host ?? url
    }
}

extension BrowserTab: MCEFBrowserDelegate {
    nonisolated func browserView(_ view: MCEFBrowserView, didChangeTitle title: String) {
        MainActor.assumeIsolated {
            guard view === chromiumView else { return }
            self.title = title
            onChange?(self)
        }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, didChangeURL url: String) {
        MainActor.assumeIsolated {
            guard view === chromiumView else { return }
            self.url = url
            onChange?(self)
        }
    }

    nonisolated func browserView(
        _ view: MCEFBrowserView, didChangeLoading isLoading: Bool, canGoBack: Bool, canGoForward: Bool
    ) {
        MainActor.assumeIsolated {
            guard view === chromiumView else { return }
            self.isLoading = isLoading
            self.canGoBack = canGoBack
            self.canGoForward = canGoForward
            onChange?(self)
        }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, wantsNewTabWithURL url: String) {
        MainActor.assumeIsolated {
            guard view === chromiumView else { return }
            onNewTabRequest?(self, url)
        }
    }

    nonisolated func browserViewDidStartDocumentLoad(_ view: MCEFBrowserView) {
        MainActor.assumeIsolated {
            guard view === chromiumView, blockedCount != 0 else { return }
            blockedCount = 0
            onChange?(self)
        }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, didBlockRequest url: String) {
        MainActor.assumeIsolated {
            guard view === chromiumView else { return }
            blockedCount += 1
            onChange?(self)
        }
    }
}
