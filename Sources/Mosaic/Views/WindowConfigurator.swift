import AppKit
import MosaicCore
import SwiftUI

/// Applies window-level parts of the Mosaic that SwiftUI cannot: the light/dark appearance
/// (so menus, popovers and AppKit controls follow it) and the traffic lights' position,
/// centred in the layout's first row at its leading edge.
struct WindowConfigurator: NSViewRepresentable {
    let colorScheme: ColorSchemePreference
    /// Height of the row the traffic lights sit in.
    let firstRowHeight: CGFloat

    func makeNSView(context: Context) -> ConfiguratorView { ConfiguratorView() }

    func updateNSView(_ view: ConfiguratorView, context: Context) {
        view.colorScheme = colorScheme
        view.firstRowHeight = firstRowHeight
        view.apply()
    }

    final class ConfiguratorView: NSView {
        var colorScheme: ColorSchemePreference = .system
        var firstRowHeight: CGFloat = 40
        private var observers: [NSObjectProtocol] = []

        override func hitTest(_ point: NSPoint) -> NSView? { nil }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observers.forEach(NotificationCenter.default.removeObserver)
            observers = []
            guard let window else { return }
            // AppKit lays the title bar out again on these; put the buttons back each time.
            let names: [NSNotification.Name] = [
                NSWindow.didResizeNotification, NSWindow.didEndLiveResizeNotification,
                NSWindow.didBecomeKeyNotification, NSWindow.didResignKeyNotification,
                NSWindow.didChangeBackingPropertiesNotification,
            ]
            observers = names.map { name in
                NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                    MainActor.assumeIsolated { self?.positionTrafficLights() }
                }
            }
            apply()
        }

        func apply() {
            guard let window else { return }
            let appearance: NSAppearance? = switch colorScheme {
            case .system: nil
            case .light: NSAppearance(named: .aqua)
            case .dark: NSAppearance(named: .darkAqua)
            }
            if window.appearance?.name != appearance?.name { window.appearance = appearance }
            positionTrafficLights()
        }

        func positionTrafficLights() {
            guard let window, !window.styleMask.contains(.fullScreen),
                  let close = window.standardWindowButton(.closeButton),
                  let titlebar = close.superview, let container = titlebar.superview
            else { return }
            let height = max(28, firstRowHeight)
            var frame = container.frame
            if frame.height != height || frame.maxY != window.frame.height {
                frame.size.height = height
                frame.origin.y = window.frame.height - height
                container.frame = frame
                titlebar.frame = container.bounds
            }
            let buttons: [NSWindow.ButtonType] = [.closeButton, .miniaturizeButton, .zoomButton]
            for (index, type) in buttons.enumerated() {
                guard let button = window.standardWindowButton(type) else { continue }
                let origin = NSPoint(x: 14 + CGFloat(index) * 20,
                                     y: ((titlebar.bounds.height - button.frame.height) / 2).rounded())
                if button.frame.origin != origin { button.setFrameOrigin(origin) }
            }
        }

        deinit {
            observers.forEach(NotificationCenter.default.removeObserver)
        }
    }
}
