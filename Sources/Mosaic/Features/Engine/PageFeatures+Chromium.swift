import AppKit
import CEFBridge
import MosaicCore

/// Receives page events from every Chromium browser (`MosaicCEF.pageDelegate`) and hands
/// each to the `PageFeatures` whose model owns the tab, so several windows can share it.
@MainActor
final class ChromiumPageRouter: NSObject {
    static let shared = ChromiumPageRouter()

    private final class WeakFeatures {
        weak var value: PageFeatures?
        init(_ value: PageFeatures) { self.value = value }
    }

    private var features: [WeakFeatures] = []

    func register(_ pageFeatures: PageFeatures) {
        features.removeAll { $0.value == nil }
        features.append(WeakFeatures(pageFeatures))
        if MosaicCEF.pageDelegate == nil, MosaicCEF.isRunning { MosaicCEF.routePopupBlockingToPageDelegate() }
        MosaicCEF.pageDelegate = self
    }

    fileprivate func route(_ view: MCEFBrowserView, _ body: (PageFeatures, BrowserTab) -> Void) {
        for entry in features {
            guard let pageFeatures = entry.value,
                  let tab = pageFeatures.model.tabs.first(where: { $0.chromiumView === view }) else { continue }
            body(pageFeatures, tab)
            return
        }
    }
}

extension ChromiumPageRouter: MCEFPageDelegate {
    nonisolated func browserViewDidCreateBrowser(_ view: MCEFBrowserView) {
        MainActor.assumeIsolated { route(view) { $0.engineAttached($1) } }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, didUpdateFindMatches count: Int, activeMatch: Int, finalUpdate: Bool) {
        MainActor.assumeIsolated {
            route(view) { $0.chromiumFindResult($1, count: count, active: activeMatch, final: finalUpdate) }
        }
    }

    nonisolated func browserViewDidStartAudio(_ view: MCEFBrowserView) {
        MainActor.assumeIsolated { route(view) { $0.chromiumAudioStarted($1) } }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, didChangeMediaAccessVideo video: Bool, audio: Bool) {
        MainActor.assumeIsolated {
            route(view) { features, tab in features.mediaInUse[tab.id] = (video || audio) ? (video, audio) : nil }
        }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, requestsPermission request: MCEFPermissionRequest) {
        MainActor.assumeIsolated {
            var handled = false
            route(view) { features, tab in
                handled = true
                features.chromiumRequests[request.identifier] = request
                features.handlePermissionRequest(Self.info(for: request), in: tab, token: .chromium(request.identifier))
            }
            if !handled { request.deny() }
        }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, didCancel request: MCEFPermissionRequest) {
        MainActor.assumeIsolated {
            route(view) { features, _ in features.cancelPermissionPrompt(token: .chromium(request.identifier)) }
        }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, didBlockPopupWithURL url: String) {
        MainActor.assumeIsolated { route(view) { $0.popupsBlocked(in: $1, urls: [url], count: 1) } }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, didChangeFullscreen fullscreen: Bool) {
        MainActor.assumeIsolated {
            route(view) { features, tab in
                if fullscreen { features.beginChromiumFullScreen(tab) } else { features.chromiumFullScreenEnded(tab) }
            }
        }
    }

    nonisolated func browserView(_ view: MCEFBrowserView, run dialog: MCEFJavaScriptDialog) {
        MainActor.assumeIsolated {
            var handled = false
            route(view) { features, tab in
                handled = true
                features.showDialog(dialog, in: tab)
            }
            if !handled { dialog.finishAccepted(false, text: nil) }
        }
    }

    nonisolated func browserViewDidResetJavaScriptDialogs(_ view: MCEFBrowserView) {
        MainActor.assumeIsolated { route(view) { $0.resetDialogs(in: $1) } }
    }

    private static func info(for request: MCEFPermissionRequest) -> PermissionRequestInfo {
        var kinds: [PermissionRequestInfo.Kind] = []
        let mask = request.kinds
        if mask.contains(.camera) { kinds.append(.camera) }
        if mask.contains(.microphone) { kinds.append(.microphone) }
        if mask.contains(.geolocation) { kinds.append(.location) }
        if mask.contains(.notifications) { kinds.append(.notifications) }
        if mask.contains(.clipboard) { kinds.append(.clipboard) }
        if mask.contains(.screenCapture) { kinds.append(.screen) }
        if mask.contains(.other) { kinds.append(.other) }
        return PermissionRequestInfo(origin: request.origin, kinds: kinds, otherNames: request.otherKinds)
    }
}

// MARK: - Page full screen

extension PageFeatures {
    /// Chromium (Alloy) leaves full screen to the app: its view moves into a window that
    /// covers the screen until the page leaves (Esc, the page's own control, a tab switch).
    func beginChromiumFullScreen(_ tab: BrowserTab) {
        guard let content = tab.chromiumView?.pageContentView else { return }
        fullScreenTabID = tab.id
        fullScreen.enter(content, screen: content.window?.screen, activate: model.allowsFocusChanges)
    }

    func chromiumFullScreenEnded(_ tab: BrowserTab) {
        fullScreen.exit()
        if fullScreenTabID == tab.id { fullScreenTabID = nil }
        if tab === selectedTab { model.focusContent() }
    }

    /// Leaves page full screen in whichever engine the tab uses.
    func endFullScreen() {
        guard let id = fullScreenTabID else { return }
        let tab = tab(withID: id)
        fullScreenTabID = nil
        fullScreen.exit()
        switch tab?.engine {
        case .chromium: tab?.chromiumView?.exitFullscreen()
        case .gecko: model.gecko.send("exitFullscreen")
        case nil: break
        }
    }
}

/// The screen-sized window a Chromium page's view lives in while the page is full screen.
@MainActor
final class PageFullScreenController {
    private var window: NSWindow?
    private weak var content: NSView?
    private weak var home: NSView?
    private var savedOptions: NSApplication.PresentationOptions = []

    var isActive: Bool { window != nil }
    var windowNumber: Int? { window?.windowNumber }

    func enter(_ view: NSView, screen: NSScreen?, activate: Bool) {
        guard window == nil, let home = view.superview, let screen = screen ?? NSScreen.main else { return }
        let window = NSWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.backgroundColor = .black
        window.isReleasedWhenClosed = false
        window.collectionBehavior = [.fullScreenAuxiliary, .canJoinAllSpaces]
        window.setFrame(screen.frame, display: false)
        window.setAccessibilityLabel("Full-screen page")
        let container = NSView(frame: NSRect(origin: .zero, size: screen.frame.size))
        container.autoresizesSubviews = true
        window.contentView = container

        self.window = window
        self.content = view
        self.home = home
        view.removeFromSuperview()
        view.frame = container.bounds
        view.autoresizingMask = [.width, .height]
        container.addSubview(view)

        savedOptions = NSApp.presentationOptions
        NSApp.presentationOptions = [.autoHideDock, .autoHideMenuBar]
        if activate { NSApp.activate(ignoringOtherApps: true) }
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(view)
    }

    func exit() {
        guard let window else { return }
        self.window = nil
        if let content, let home {
            content.removeFromSuperview()
            content.frame = home.bounds
            content.autoresizingMask = [.width, .height]
            home.addSubview(content)
            home.window?.makeFirstResponder(content)
        }
        NSApp.presentationOptions = savedOptions
        window.orderOut(nil)
        window.close()
    }
}
