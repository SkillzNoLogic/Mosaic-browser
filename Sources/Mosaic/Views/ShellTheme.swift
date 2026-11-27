import AppKit
import MosaicCore
import SwiftUI

// Visual building blocks shared by the shell's views: colours derived from the Mosaic's
// appearance, size metrics from its density (and the Accessibility text size), Liquid Glass
// with a solid fallback, and the toolbar button style.

// MARK: - Colours

extension Color {
    /// A colour from a `#RRGGBB(AA)` string; `"system"` is the system accent colour.
    init(hex: String, fallback: Color = .accentColor) {
        if hex == HexColor.system {
            self = Color(nsColor: .controlAccentColor)
        } else if let c = HexColor.components(hex) {
            self = Color(.sRGB, red: c.red, green: c.green, blue: c.blue, opacity: c.alpha)
        } else {
            self = fallback
        }
    }

    /// A colour that follows the window's light/dark appearance.
    static func dynamic(light: NSColor, dark: NSColor) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
        })
    }
}

enum ShellColors {
    static func tint(for engine: EngineKind) -> Color {
        switch engine {
        case .gecko: Color(red: 1.0, green: 0.45, blue: 0.1)
        case .chromium: Color(red: 0.2, green: 0.52, blue: 0.96)
        }
    }

    /// Chrome's toolbar and the selected tab that merges into it.
    static let surface = Color.dynamic(
        light: .white, dark: NSColor(srgbRed: 0.235, green: 0.239, blue: 0.251, alpha: 1))
    /// Chrome's darker frame behind the tabs.
    static let frame = Color.dynamic(
        light: NSColor(srgbRed: 0.871, green: 0.882, blue: 0.902, alpha: 1),
        dark: NSColor(srgbRed: 0.125, green: 0.129, blue: 0.137, alpha: 1))
    /// The page background behind Mosaic's own pages.
    static let page = Color(nsColor: .textBackgroundColor)
    /// Opaque panel for things drawn over the page (suggestions, peeking sidebar, notices):
    /// the Gecko page is another window, so translucency there would show it unblurred.
    static let overlayPanel = Color.dynamic(
        light: NSColor(srgbRed: 0.98, green: 0.98, blue: 0.985, alpha: 1),
        dark: NSColor(srgbRed: 0.16, green: 0.16, blue: 0.17, alpha: 1))
    /// Fill for controls on solid chrome.
    static let control = Color.dynamic(
        light: NSColor(white: 0, alpha: 0.055), dark: NSColor(white: 1, alpha: 0.085))
    /// Selected row in a sidebar.
    static let sidebarSelection = Color.dynamic(
        light: NSColor(white: 1, alpha: 0.82), dark: NSColor(white: 1, alpha: 0.13))
}

// MARK: - Metrics

/// Sizes for the current density and text size. Primary targets stay at least 32×32 pt,
/// and every toolbar button's hit area spans the full height of its row.
struct ShellMetrics: Equatable {
    var scale: CGFloat
    var density: ShellDensity

    private var step: CGFloat {
        switch density {
        case .compact: -1
        case .regular: 0
        case .comfortable: 1
        }
    }

    var rowHeight: CGFloat { (44 + step * 6) * scale }
    var unifiedRowHeight: CGFloat { (52 + step * 6) * scale }
    var tabRowHeight: CGFloat { (40 + step * 4) * scale }
    var segmentedTabBarHeight: CGFloat { (32 + step * 3) * scale }
    var button: CGFloat { (32 + step * 3) * scale }
    var icon: CGFloat { (15 + step) * scale }
    var font: CGFloat { (13 + step * 0.5) * scale }
    var smallFont: CGFloat { (11.5 + step * 0.5) * scale }
    var fieldHeight: CGFloat { (32 + step * 3) * scale }
    var tabHeight: CGFloat { (32 + step * 3) * scale }
    var sidebarRowHeight: CGFloat { (34 + step * 3) * scale }
    var bookmarksBarHeight: CGFloat { (30 + step * 2) * scale }
    var railWidth: CGFloat { (52 + step * 4) * scale }
    var trafficLightsWidth: CGFloat { 78 }
    var titleStripHeight: CGFloat { 38 }
}

// MARK: - Theme

/// Everything a shell view needs to draw itself for the current Mosaic.
struct ShellTheme {
    var layout: MosaicLayout
    var accent: Color
    var glassEnabled: Bool
    var reduceMotion: Bool
    var prominentFocusRings: Bool
    var alwaysShowTabCloseButtons: Bool

    var appearance: MosaicAppearance { layout.appearance }
    var cornerRadius: CGFloat { appearance.cornerRadius }

    /// Animation that honours Reduce Motion.
    func animation(_ animation: Animation = .smooth(duration: 0.22)) -> Animation? {
        reduceMotion ? nil : animation
    }

    static let fallback = ShellTheme(
        layout: .preset(.chrome), accent: .accentColor, glassEnabled: false, reduceMotion: false,
        prominentFocusRings: false, alwaysShowTabCloseButtons: false)
}

extension EnvironmentValues {
    @Entry var shellTheme = ShellTheme.fallback
    @Entry var shellMetrics = ShellMetrics(scale: 1, density: .regular)
}

/// The window background described by a `BackgroundSpec`. A light colour or gradient is
/// dimmed in dark mode (and a dark one lifted in light mode), so chrome text stays legible
/// whichever appearance is in effect.
struct BackgroundFill: View {
    @Environment(\.colorScheme) private var colorScheme
    let spec: BackgroundSpec

    var body: some View {
        switch spec.kind {
        case .system:
            Color(nsColor: .windowBackgroundColor)
        case .solid:
            Color(hex: spec.color, fallback: Color(nsColor: .windowBackgroundColor))
                .overlay(adaptation(luminance: Self.luminance(of: [spec.color])))
        case .gradient:
            // canvas: Gradient Studio painting (mesh, strength, grain, Raw), Features/Canvas/GradientRendering.swift.
            CanvasGradientBackground(spec: spec.gradient)
        }
    }

    @ViewBuilder private func adaptation(luminance: Double) -> some View {
        if colorScheme == .dark, luminance > 0.55 {
            Color.black.opacity(0.66)
        } else if colorScheme == .light, luminance < 0.35 {
            Color.white.opacity(0.62)
        }
    }

    /// Average relative luminance of the colours (0 black … 1 white).
    static func luminance(of colors: [String]) -> Double {
        let values = colors.compactMap(HexColor.components).map { 0.2126 * $0.red + 0.7152 * $0.green + 0.0722 * $0.blue }
        return values.isEmpty ? 0.5 : values.reduce(0, +) / Double(values.count)
    }
}

struct GradientFill: View {
    let spec: GradientSpec

    // canvas: every kind, including the Gradient Studio's mesh (Features/Canvas/GradientRendering.swift).
    var body: some View { CanvasGradientFill(spec: spec) }
}

// MARK: - Liquid Glass / solid surfaces

enum SurfaceRole {
    /// A control on the chrome (address field, button cluster).
    case control
    /// A larger panel (sidebar card, settings card).
    case panel
    /// The selected item (tab, row).
    case selected
}

/// Liquid Glass when the Mosaic asks for it and transparency is not reduced (macOS 26+);
/// otherwise a solid fill.
struct ChromeSurface<S: InsettableShape>: ViewModifier {
    @Environment(\.shellTheme) private var theme
    let shape: S
    let role: SurfaceRole
    var tint: Color?
    var interactive = false

    func body(content: Content) -> some View {
        if theme.glassEnabled, #available(macOS 26.0, *) {
            var glass = Glass.regular
            if let tint { glass = glass.tint(tint) }
            if interactive { glass = glass.interactive() }
            return AnyView(content.glassEffect(glass, in: shape))
        }
        return AnyView(content.background(shape.fill(solidFill)))
    }

    private var solidFill: AnyShapeStyle {
        if let tint { return AnyShapeStyle(tint.opacity(0.22)) }
        switch role {
        case .control: return AnyShapeStyle(ShellColors.control)
        case .panel: return AnyShapeStyle(ShellColors.overlayPanel)
        case .selected: return AnyShapeStyle(ShellColors.sidebarSelection)
        }
    }
}

extension View {
    func chromeSurface<S: InsettableShape>(_ shape: S, role: SurfaceRole = .control, tint: Color? = nil,
                                           interactive: Bool = false) -> some View {
        modifier(ChromeSurface(shape: shape, role: role, tint: tint, interactive: interactive))
    }

    /// Groups adjacent glass surfaces so they blend (no-op without glass).
    @ViewBuilder func glassGroup(spacing: CGFloat = 6, enabled: Bool) -> some View {
        if enabled, #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: spacing) { self }
        } else {
            self
        }
    }
}

// MARK: - Buttons

/// Toolbar icon button: generous hit area, hover and pressed states, and a focus ring.
struct ToolbarButtonStyle: ButtonStyle {
    var width: CGFloat?
    var height: CGFloat?

    func makeBody(configuration: Configuration) -> some View {
        StyledToolbarButton(configuration: configuration, width: width, height: height)
    }

    private struct StyledToolbarButton: View {
        let configuration: Configuration
        let width: CGFloat?
        let height: CGFloat?
        @Environment(\.shellTheme) private var theme
        @Environment(\.shellMetrics) private var metrics
        @Environment(\.isEnabled) private var isEnabled
        @Environment(\.isFocused) private var isFocused
        @State private var hovering = false

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: max(5, min(theme.cornerRadius, 10)), style: .continuous)
            configuration.label
                .font(.system(size: metrics.icon, weight: .medium))
                .foregroundStyle(isEnabled ? Color.primary.opacity(0.82) : Color.primary.opacity(0.28))
                .frame(minWidth: width ?? metrics.button, minHeight: height ?? metrics.button)
                .padding(.horizontal, width == nil ? 0 : 0)
                .background(shape.fill(fill))
                .overlay {
                    if isFocused, theme.prominentFocusRings {
                        shape.strokeBorder(theme.accent, lineWidth: 3)
                    }
                }
                .contentShape(Rectangle())
                .onHover { hovering = $0 }
                .animation(theme.animation(.easeOut(duration: 0.12)), value: hovering)
        }

        private var fill: Color {
            guard isEnabled else { return .clear }
            if configuration.isPressed { return Color.primary.opacity(0.16) }
            return hovering ? Color.primary.opacity(0.08) : .clear
        }
    }
}

/// A plain row button for menus and sidebars, with hover feedback.
struct HoverRowStyle: ButtonStyle {
    var cornerRadius: CGFloat = 7

    func makeBody(configuration: Configuration) -> some View {
        Row(configuration: configuration, cornerRadius: cornerRadius)
    }

    private struct Row: View {
        let configuration: Configuration
        let cornerRadius: CGFloat
        @Environment(\.shellTheme) private var theme
        @Environment(\.isFocused) private var isFocused
        @State private var hovering = false

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            configuration.label
                .contentShape(Rectangle())
                .background(shape.fill(Color.primary.opacity(configuration.isPressed ? 0.14 : hovering ? 0.07 : 0)))
                .overlay {
                    if isFocused, theme.prominentFocusRings { shape.strokeBorder(theme.accent, lineWidth: 3) }
                }
                .onHover { hovering = $0 }
        }
    }
}

// MARK: - Window dragging

/// Empty chrome that moves the window when dragged and zooms it on double-click, like a
/// title bar.
struct WindowDragArea: NSViewRepresentable {
    func makeNSView(context: Context) -> DragView { DragView() }
    func updateNSView(_ nsView: DragView, context: Context) {}

    final class DragView: NSView {
        override var mouseDownCanMoveWindow: Bool { true }
        override func mouseDown(with event: NSEvent) {
            if event.clickCount == 2 {
                window?.performZoom(nil)
            } else {
                window?.performDrag(with: event)
            }
        }
    }
}
