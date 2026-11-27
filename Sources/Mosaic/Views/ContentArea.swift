import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

/// The page area of the window: the selected tab (Chromium view, the hole Gecko's window
/// shows through, Mosaic's own pages, the start page) or Settings.
///
/// The card's screen rectangle is reported continuously; that is what keeps the docked
/// Gecko window exactly behind it in every Mosaic (see `ContentRectRelay`).
struct ContentCard: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    let relay: ContentRectRelay

    var body: some View {
        let radius = theme.appearance.contentCornerRadius
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        ZStack {
            // Always present, never drawn: tells the model where the page area is on screen.
            ContentRectReporter(relay: relay)
            if let category = model.settingsCategory {
                SettingsView(selection: category)
                    .transition(.opacity)
            } else if let tab = model.selectedTab {
                TabContent(tab: tab, gecko: model.gecko, cornerRadius: radius)
                    .id(tab.id)
            } else {
                ShellColors.page
            }
        }
        .clipShape(shape)
        .overlay(alignment: .top) {
            // Engine area's find bar, over the top of the page.
            if model.settingsCategory == nil { FindBar() }
        }
        .overlay {
            if theme.appearance.glow {
                shape.strokeBorder(theme.accent.opacity(0.85), lineWidth: 1.5)
                    .shadow(color: theme.accent.opacity(0.7), radius: 9)
                    .allowsHitTesting(false)
            }
        }
        .shadow(color: .black.opacity(theme.appearance.contentInset > 0 && !theme.appearance.glow ? 0.16 : 0),
                radius: 5, y: 1)
        .animation(theme.animation(.easeOut(duration: 0.15)), value: model.settingsCategory)
    }
}

private struct TabContent: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var tab: BrowserTab
    @ObservedObject var gecko: GeckoEngine
    let cornerRadius: CGFloat

    var body: some View {
        if let page = tab.internalPage {
            InternalPageView(page: page)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(ShellColors.page)
        } else if tab.isStartPage {
            StartPage()
        } else if tab.engine == .gecko, model.geckoRuntimeMissing {
            GeckoRuntimeMissingView(tab: tab)  // platform: offers the in-app Gecko install
        } else if let failure = tab.failure {
            EngineMessage(
                symbol: "exclamationmark.triangle", title: "This page can’t be shown",
                detail: failure, actionTitle: "Reload") { model.reload() }
        } else {
            switch tab.engine {
            case .chromium:
                if let view = tab.chromiumView {
                    ChromiumContainer(browserView: view, cornerRadius: cornerRadius)
                } else {
                    EngineMessage(symbol: "hourglass", title: "Starting Chromium…", detail: nil, showsProgress: true)
                }
            case .gecko:
                if model.shellGeckoRuntimeMissing {
                    // Platform area's install offer.
                    ShellGeckoRuntimeMissingMount(tab: tab)
                } else if gecko.status == .running, tab.geckoID != nil {
                    // Nothing at all: these pixels stay transparent, so the docked Gecko
                    // window shows through and receives the clicks.
                    GeckoHole()
                } else {
                    EngineMessage(
                        symbol: "hourglass",
                        title: gecko.status == .running ? "Opening page…" : "Starting Mozilla engine…",
                        detail: gecko.runtime.map { "\($0.name) \($0.version)" },
                        showsProgress: true)
                }
            }
        }
    }
}

struct EngineMessage: View {
    let symbol: String
    let title: String
    let detail: String?
    var showsProgress = false
    var actionTitle: String?
    var action: (() -> Void)?

    init(symbol: String, title: String, detail: String?, showsProgress: Bool = false,
         actionTitle: String? = nil, action: (() -> Void)? = nil) {
        self.symbol = symbol
        self.title = title
        self.detail = detail
        self.showsProgress = showsProgress
        self.actionTitle = actionTitle
        self.action = action
    }

    var body: some View {
        VStack(spacing: 12) {
            if showsProgress {
                ProgressView()
            } else {
                Image(systemName: symbol).font(.system(size: 30)).foregroundStyle(.secondary)
                    .accessibilityHidden(true)
            }
            Text(title).font(.title3.weight(.medium))
            if let detail {
                Text(detail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 440)
                    .textSelection(.enabled)
            }
            if let actionTitle, let action {
                Button(actionTitle, action: action).keyboardShortcut(.defaultAction)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(ShellColors.page)
        .accessibilityElement(children: .contain)
    }
}

// MARK: - Chromium

/// Hosts the selected tab's Chromium view. The view belongs to the tab: when the layout
/// changes, it is moved into the new container, never recreated, so the page keeps running.
private struct ChromiumContainer: NSViewRepresentable {
    let browserView: MCEFBrowserView
    let cornerRadius: CGFloat

    func makeNSView(context: Context) -> NSView {
        let container = NSView()
        container.autoresizesSubviews = true
        container.wantsLayer = true
        container.layer?.masksToBounds = true
        return container
    }

    func updateNSView(_ container: NSView, context: Context) {
        container.layer?.cornerRadius = cornerRadius
        container.layer?.cornerCurve = .continuous
        guard browserView.superview !== container else { return }
        // Moving an NSView between superviews keeps its browser alive (it is created once,
        // on first entering a window; see MCEFBrowserView).
        browserView.removeFromSuperview()
        browserView.frame = container.bounds
        browserView.autoresizingMask = [.width, .height]
        container.addSubview(browserView)
        Self.refreshSize(of: browserView, in: container)
    }

    /// Chromium only re-lays out its page for size changes it sees while in a window; one
    /// made while the view was detached (another tab or another layout was showing) would
    /// leave the page at its old size. Once the view is back, change its size by a point
    /// and restore it, in two separate turns, so Chromium gets a fresh resize.
    private static func refreshSize(of view: MCEFBrowserView, in container: NSView) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak view, weak container] in
            guard let view, let container, view.superview === container, view.window != nil else { return }
            var nudged = container.bounds
            nudged.size.height = max(1, nudged.height - 1)
            view.frame = nudged
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak view, weak container] in
                guard let view, let container, view.superview === container else { return }
                view.frame = container.bounds
            }
        }
    }

    static func dismantleNSView(_ container: NSView, coordinator: ()) {
        // The browser view belongs to its tab and outlives this container. Only detach it
        // if it is still here (it may already have moved to the next container).
        for view in container.subviews { view.removeFromSuperview() }
    }
}

// MARK: - Gecko

/// The see-through content area of a Gecko tab.
private struct GeckoHole: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { HoleView() }
    func updateNSView(_ view: NSView, context: Context) {}

    /// The window's shadow follows its opaque pixels, so it has to be
    /// recomputed whenever the hole opens or closes.
    final class HoleView: NSView {
        override var isOpaque: Bool { false }
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            window?.invalidateShadow()
        }
        override func viewWillMove(toWindow newWindow: NSWindow?) {
            super.viewWillMove(toWindow: newWindow)
            if newWindow == nil, let window {
                DispatchQueue.main.async { window.invalidateShadow() }
            }
        }
    }
}

// MARK: - Content rectangle

/// Passes the page area's screen rectangle to the model. When the layout changes, the old
/// reporter goes away and a new one appears in the same turn of the run loop; the relay
/// waits for that before reporting "no content area", so the Gecko window is re-docked
/// to the new rectangle instead of being hidden and shown again.
@MainActor
final class ContentRectRelay {
    /// The last rectangle sent, for diagnostics (`dump`).
    private(set) var lastRect: CGRect?
    private weak var current: DockAnchorView?
    private let sink: (CGRect?) -> Void

    init(sink: @escaping (CGRect?) -> Void) {
        self.sink = sink
    }

    fileprivate func report(_ rect: CGRect, from view: DockAnchorView) {
        current = view
        send(rect)
    }

    fileprivate func detach(_ view: DockAnchorView) {
        guard current === view else { return }
        current = nil
        DispatchQueue.main.async { [weak self] in
            guard let self, self.current == nil else { return }
            self.send(nil)
        }
    }

    private func send(_ rect: CGRect?) {
        guard rect != lastRect else { return }
        lastRect = rect
        sink(rect)
    }
}

/// An invisible view that reports its own rect in screen coordinates.
private struct ContentRectReporter: NSViewRepresentable {
    let relay: ContentRectRelay

    func makeNSView(context: Context) -> DockAnchorView {
        let view = DockAnchorView()
        view.relay = relay
        return view
    }

    func updateNSView(_ view: DockAnchorView, context: Context) {
        view.relay = relay
        view.report()
    }

    static func dismantleNSView(_ view: DockAnchorView, coordinator: ()) {
        view.detach()
    }
}

final class DockAnchorView: NSView {
    fileprivate weak var relay: ContentRectRelay?
    private var observers: [NSObjectProtocol] = []

    override var isOpaque: Bool { false }
    // Never takes clicks: the page (or the Gecko window behind) gets them.
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        stopObserving()
        guard let window else {
            relay?.detach(self)
            return
        }
        let center = NotificationCenter.default
        let names: [NSNotification.Name] = [
            NSWindow.didMoveNotification, NSWindow.didResizeNotification,
            NSWindow.didChangeScreenNotification, NSWindow.didEndLiveResizeNotification,
        ]
        observers = names.map { name in
            center.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.report() }
            }
        }
        report()
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        report()
    }

    override func setFrameOrigin(_ newOrigin: NSPoint) {
        super.setFrameOrigin(newOrigin)
        report()
    }

    override func layout() {
        super.layout()
        report()
    }

    func report() {
        guard let window, bounds.width > 0, bounds.height > 0 else { return }
        relay?.report(window.convertToScreen(convert(bounds, to: nil)), from: self)
    }

    func detach() {
        stopObserving()
        relay?.detach(self)
    }

    private func stopObserving() {
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
    }
}

// MARK: - Start page

/// The New Tab page: a search field, then the productivity area's Speed Dial / shortcuts.
struct StartPage: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    @State private var query = ""
    @FocusState private var focused: Bool

    var body: some View {
        ScrollView {
            VStack(spacing: 22) {
                Spacer(minLength: 60)
                MosaicMark().frame(width: 56, height: 56)
                HStack(spacing: 10) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                        .accessibilityHidden(true)
                    TextField("Search \(model.searchEngine.name) or enter address", text: $query)
                        .textFieldStyle(.plain)
                        .font(.system(size: 16 * metrics.scale))
                        .focused($focused)
                        .onSubmit {
                            let input = query
                            query = ""
                            model.navigate(input)
                        }
                        .accessibilityLabel("Search or enter address")
                }
                .padding(.horizontal, 16)
                .frame(maxWidth: 560)
                .frame(height: 46 * metrics.scale)
                .background(RoundedRectangle(cornerRadius: max(10, theme.cornerRadius + 4), style: .continuous)
                    .fill(Color(nsColor: .controlBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: max(10, theme.cornerRadius + 4), style: .continuous)
                    .strokeBorder(focused ? theme.accent : Color.primary.opacity(0.12), lineWidth: focused ? 2 : 1))
                .shadow(color: .black.opacity(0.06), radius: 8, y: 2)
                .padding(.horizontal, 24)

                HStack(spacing: 6) {
                    Circle().fill(ShellColors.tint(for: model.engine)).frame(width: 8, height: 8)
                        .accessibilityHidden(true)
                    Text("New tabs open in \(model.engine.displayName) · searching with \(model.searchEngine.name)")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }

                // Productivity area's new-tab content (Speed Dial, shortcuts).
                StartPageContent()
                    .frame(maxWidth: 900)
                    .padding(.horizontal, 24)
                Spacer(minLength: 40)
            }
            .frame(maxWidth: .infinity)
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(startBackground)
    }

    @ViewBuilder private var startBackground: some View {
        // canvas: the New Tab page's background from Settings › Mosaics (Gradient Studio).
        let newTab = theme.appearance.newTabBackground
        switch newTab.mode {
        case .window: BackgroundFill(spec: theme.appearance.background(forWorkspace: model.selectedWorkspaceID.uuidString))
        case .custom: BackgroundFill(spec: newTab.custom)
        case .plain:
            // A whisper of the Mosaic's accent so the new-tab page belongs to the theme.
            ZStack {
                ShellColors.page
                LinearGradient(colors: [theme.accent.opacity(0.07), .clear], startPoint: .top, endPoint: .center)
            }
        }
    }
}

/// Four tiles in the two engine colours.
struct MosaicMark: View {
    var body: some View {
        GeometryReader { proxy in
            let side = min(proxy.size.width, proxy.size.height)
            let gap = side * 0.08
            let tile = (side - gap) / 2
            let radius = tile * 0.24
            VStack(spacing: gap) {
                HStack(spacing: gap) {
                    RoundedRectangle(cornerRadius: radius).fill(ShellColors.tint(for: .gecko))
                    RoundedRectangle(cornerRadius: radius).fill(ShellColors.tint(for: .chromium).opacity(0.55))
                }
                HStack(spacing: gap) {
                    RoundedRectangle(cornerRadius: radius).fill(ShellColors.tint(for: .gecko).opacity(0.55))
                    RoundedRectangle(cornerRadius: radius).fill(ShellColors.tint(for: .chromium))
                }
            }
            .frame(width: side, height: side)
        }
        .accessibilityHidden(true)
    }
}
