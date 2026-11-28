import SwiftUI

/// Colours and names for drawing tab organisation; the shell uses these too so groups
/// look the same everywhere.
extension TabGroup.Color {
    var swatch: SwiftUI.Color {
        switch self {
        case .grey: SwiftUI.Color(nsColor: .systemGray)
        case .blue: SwiftUI.Color(nsColor: .systemBlue)
        case .red: SwiftUI.Color(nsColor: .systemRed)
        case .yellow: SwiftUI.Color(nsColor: .systemYellow)
        case .green: SwiftUI.Color(nsColor: .systemGreen)
        case .pink: SwiftUI.Color(nsColor: .systemPink)
        case .purple: SwiftUI.Color(nsColor: .systemPurple)
        case .cyan: SwiftUI.Color(nsColor: .systemTeal)
        case .orange: SwiftUI.Color(nsColor: .systemOrange)
        }
    }

    var displayName: String { rawValue.prefix(1).uppercased() + rawValue.dropFirst() }
}

extension TabGroup {
    /// What to show for a group without a name.
    var displayName: String { name.isEmpty ? "Unnamed group" : name }
}

extension BrowserTab {
    /// Opacity the shell applies to a sleeping tab's title and icon.
    static let asleepOpacity = 0.55
}

enum TabsStyle {
    /// The engine dot's colour (orange Mozilla, blue Chromium), as in the tab strip.
    static func engineTint(_ engine: EngineKind) -> SwiftUI.Color {
        switch engine {
        case .gecko: SwiftUI.Color(red: 1.0, green: 0.45, blue: 0.1)
        case .chromium: SwiftUI.Color(red: 0.2, green: 0.52, blue: 0.96)
        }
    }

    /// SF Symbols offered for workspaces.
    static let workspaceSymbols = [
        "person", "briefcase", "house", "book", "graduationcap", "hammer", "paintbrush",
        "gamecontroller", "cart", "heart", "star", "globe", "square.grid.2x2",
    ]
}

/// A floating card in the tabs area's overlays: glass where available, solid when the
/// user asked for reduced transparency.
struct TabsCardBackground: ViewModifier {
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    var cornerRadius: CGFloat = 14

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        content
            .background {
                if reduceTransparency {
                    shape.fill(SwiftUI.Color(nsColor: .windowBackgroundColor))
                } else {
                    shape.fill(.regularMaterial)
                }
            }
            .overlay(shape.strokeBorder(.quaternary))
            .clipShape(shape)
            .shadow(color: .black.opacity(0.18), radius: 18, y: 6)
    }
}

extension View {
    func tabsCard(cornerRadius: CGFloat = 14) -> some View {
        modifier(TabsCardBackground(cornerRadius: cornerRadius))
    }
}
