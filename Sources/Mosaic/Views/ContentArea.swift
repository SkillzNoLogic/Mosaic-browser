import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

/// Shows the selected tab: the start page, a Chromium view, or the hole that
/// the docked Gecko window fills.
struct ContentArea: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        ZStack {
            // Always present, never drawn: tells the model where the content
            // area is on screen so Gecko's window can be kept exactly there.
            ContentRectReporter { model.setContentRect($0) }
            if let tab = model.selectedTab {
                TabContent(tab: tab, gecko: model.gecko)
                    .id(tab.id)
            } else {
                Chrome.background
            }
        }
    }
}

private struct TabContent: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var tab: BrowserTab
    @ObservedObject var gecko: GeckoEngine

    var body: some View {
        if tab.isStartPage {
            StartPage()
        } else if let failure = tab.failure {
            EngineMessage(
                symbol: "exclamationmark.triangle", title: "This page can’t be shown",
                detail: failure, actionTitle: "Reload") { model.reload() }
        } else {
            switch tab.engine {
            case .chromium:
                if let view = tab.chromiumView {
                    ChromiumContainer(browserView: view)
                } else {
                    EngineMessage(symbol: "hourglass", title: "Starting Chromium…", detail: nil)
                }
            case .gecko:
                if gecko.status == .running, tab.geckoID != nil {
                    // Nothing at all: these pixels stay transparent, so the
                    // docked Gecko window shows through and receives the clicks.
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
        .background(Color(nsColor: .textBackgroundColor))
    }
}

// MARK: - Chromium

/// Hosts the selected tab's Chromium view.
private struct ChromiumContainer: NSViewRepresentable {
    let browserView: MCEFBrowserView

    func makeNSView(context: Context) -> NSView {
        let container = NSView()
        container.autoresizesSubviews = true
        return container
    }

    func updateNSView(_ container: NSView, context: Context) {
        guard browserView.superview !== container else { return }
        container.subviews.forEach { $0.removeFromSuperview() }
        browserView.frame = container.bounds
        browserView.autoresizingMask = [.width, .height]
        container.addSubview(browserView)
    }

    static func dismantleNSView(_ container: NSView, coordinator: ()) {
        // The browser view belongs to its tab and outlives this container.
        container.subviews.forEach { $0.removeFromSuperview() }
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

/// An invisible view that reports its own rect in screen coordinates.
private struct ContentRectReporter: NSViewRepresentable {
    let onRect: (CGRect?) -> Void

    func makeNSView(context: Context) -> DockAnchorView {
        let view = DockAnchorView()
        view.onRect = onRect
        return view
    }

    func updateNSView(_ view: DockAnchorView, context: Context) {
        view.onRect = onRect
        view.report()
    }

    static func dismantleNSView(_ view: DockAnchorView, coordinator: ()) {
        view.detach()
    }
}

final class DockAnchorView: NSView {
    var onRect: ((CGRect?) -> Void)?
    private var observers: [NSObjectProtocol] = []
    private var lastReported: CGRect?
    private var hasReported = false

    override var isOpaque: Bool { false }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        stopObserving()
        guard let window else {
            send(nil)
            return
        }
        let center = NotificationCenter.default
        let names: [NSNotification.Name] = [
            NSWindow.didMoveNotification, NSWindow.didResizeNotification,
            NSWindow.didChangeScreenNotification, NSWindow.didEndLiveResizeNotification,
        ]
        observers = names.map { name in
            center.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                self?.report()
            }
        }
        report()
    }

    override func layout() {
        super.layout()
        report()
    }

    func report() {
        guard let window else { return }
        send(window.convertToScreen(convert(bounds, to: nil)))
    }

    func detach() {
        stopObserving()
        send(nil)
        onRect = nil
    }

    private func send(_ rect: CGRect?) {
        guard !hasReported || rect != lastReported else { return }
        hasReported = true
        lastReported = rect
        onRect?(rect)
    }

    private func stopObserving() {
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
    }
}

// MARK: - Start page

struct StartPage: View {
    @EnvironmentObject private var model: BrowserModel
    private var query = State(initialValue: "")

    var body: some View {
        VStack(spacing: 22) {
            Spacer()
            MosaicMark().frame(width: 64, height: 64)
            Text("Mosaic").font(.system(size: 34, weight: .semibold, design: .rounded))

            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Search \(model.searchEngine.name) or enter address", text: query.projectedValue)
                    .textFieldStyle(.plain)
                    .font(.system(size: 15))
                    .onSubmit { model.navigate(query.wrappedValue) }
            }
            .padding(.horizontal, 14)
            .frame(width: 520, height: 42)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.12)))

            HStack(spacing: 6) {
                Circle().fill(Chrome.tint(for: model.engine)).frame(width: 8, height: 8)
                Text("New tabs open in \(model.engine.displayName) · searching with \(model.searchEngine.name)")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .textBackgroundColor))
    }
}

/// Four tiles in the two engine colours.
struct MosaicMark: View {
    var body: some View {
        GeometryReader { proxy in
            let gap = proxy.size.width * 0.08
            let tile = (proxy.size.width - gap) / 2
            let radius = tile * 0.24
            VStack(spacing: gap) {
                HStack(spacing: gap) {
                    RoundedRectangle(cornerRadius: radius).fill(Chrome.tint(for: .gecko))
                    RoundedRectangle(cornerRadius: radius).fill(Chrome.tint(for: .chromium).opacity(0.55))
                }
                HStack(spacing: gap) {
                    RoundedRectangle(cornerRadius: radius).fill(Chrome.tint(for: .gecko).opacity(0.55))
                    RoundedRectangle(cornerRadius: radius).fill(Chrome.tint(for: .chromium))
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.width)
            .frame(height: tile * 2 + gap)
        }
        .accessibilityHidden(true)
    }
}
