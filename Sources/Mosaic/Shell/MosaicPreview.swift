import MosaicCore
import SwiftUI

/// A miniature of a layout, drawn from the same `MosaicLayout` the window uses, so a
/// customised preset previews exactly as it is configured.
struct MosaicPreview: View {
    @Environment(\.colorScheme) private var systemScheme
    let layout: MosaicLayout

    var body: some View {
        GeometryReader { proxy in
            let s = proxy.size.width / 320
            let a = layout.appearance
            let accent = Color(hex: a.accent)
            let ink = Color.primary.opacity(0.22)
            let titleItems = layout.drawnItems(in: .titleBar, showsHome: true)
            let toolbarItems = layout.drawnItems(in: .toolbar, showsHome: true)
            let tabBarItems = layout.drawnItems(in: .tabBar, showsHome: true)
            let railItems = layout.drawnItems(in: .rail, showsHome: true)
            let vertical = layout.tabPlacement.isVertical
            let sidebarWidth = vertical ? 84 * s : 0
            let inset = a.contentInset * s * 0.8
            let radius = a.contentCornerRadius * s * 0.8

            ZStack(alignment: .topLeading) {
                BackgroundFill(spec: a.background)
                VStack(spacing: 0) {
                    if !titleItems.isEmpty {
                        row(titleItems, height: 22 * s, s: s, accent: accent, ink: ink, lights: true)
                            .background(a.toolbarSurface && a.background.kind == .system ? ShellColors.frame : .clear)
                    }
                    HStack(spacing: 0) {
                        if !railItems.isEmpty {
                            VStack(spacing: 7 * s) {
                                ForEach(railItems.filter { $0.component != .flexibleSpace }.prefix(6)) { _ in
                                    Circle().fill(ink).frame(width: 7 * s, height: 7 * s)
                                }
                                Spacer()
                            }
                            .padding(.top, 8 * s)
                            .frame(width: 20 * s)
                        }
                        if vertical, layout.tabPlacement == .sidebarLeading {
                            sidebar(s: s, accent: accent, ink: ink, width: sidebarWidth, lights: titleItems.isEmpty)
                        }
                        VStack(spacing: 0) {
                            if !toolbarItems.isEmpty {
                                row(toolbarItems, height: 20 * s, s: s, accent: accent, ink: ink,
                                    lights: titleItems.isEmpty && !vertical && railItems.isEmpty)
                                    .background(a.toolbarSurface ? ShellColors.surface : .clear)
                            } else if titleItems.isEmpty && !vertical {
                                Color.clear.frame(height: 14 * s)
                            }
                            if !tabBarItems.isEmpty {
                                row(tabBarItems, height: 15 * s, s: s, accent: accent, ink: ink, lights: false)
                            }
                            if layout.bookmarksBar {
                                HStack(spacing: 6 * s) {
                                    ForEach(0..<5, id: \.self) { _ in
                                        Capsule().fill(ink.opacity(0.7)).frame(width: 18 * s, height: 3 * s)
                                    }
                                    Spacer()
                                }
                                .padding(.horizontal, 8 * s)
                                .frame(height: 10 * s)
                                .background(a.toolbarSurface ? ShellColors.surface : .clear)
                            }
                            page(s: s, accent: accent)
                                .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
                                .overlay {
                                    if a.glow {
                                        RoundedRectangle(cornerRadius: radius, style: .continuous)
                                            .strokeBorder(accent, lineWidth: 1)
                                            .shadow(color: accent, radius: 4)
                                    }
                                }
                                .shadow(color: .black.opacity(inset > 0 ? 0.15 : 0), radius: 2 * s, y: 0.5 * s)
                                .padding(inset)
                        }
                        if vertical, layout.tabPlacement == .sidebarTrailing {
                            sidebar(s: s, accent: accent, ink: ink, width: sidebarWidth, lights: false)
                        }
                    }
                }
            }
            .environment(\.colorScheme, scheme)
        }
        .aspectRatio(16 / 10, contentMode: .fit)
        .accessibilityHidden(true)
    }

    private var scheme: ColorScheme {
        switch layout.appearance.colorScheme {
        case .system: systemScheme
        case .light: .light
        case .dark: .dark
        }
    }

    private func lightsView(s: CGFloat) -> some View {
        HStack(spacing: 3 * s) {
            Circle().fill(Color(red: 1, green: 0.37, blue: 0.34))
            Circle().fill(Color(red: 1, green: 0.74, blue: 0.18))
            Circle().fill(Color(red: 0.16, green: 0.79, blue: 0.25))
        }
        .frame(width: 22 * s, height: 5 * s)
    }

    @ViewBuilder
    private func row(_ items: [RegionItem], height: CGFloat, s: CGFloat, accent: Color, ink: Color, lights: Bool) -> some View {
        HStack(spacing: 4 * s) {
            if lights { lightsView(s: s).padding(.trailing, 4 * s) }
            ForEach(items) { item in
                element(item.component, s: s, accent: accent, ink: ink, height: height)
            }
        }
        .padding(.horizontal, 6 * s)
        .frame(height: height)
    }

    @ViewBuilder
    private func element(_ component: ShellComponent, s: CGFloat, accent: Color, ink: Color, height: CGFloat) -> some View {
        let r = layout.appearance.cornerRadius * s * 0.5
        switch component {
        case .addressField:
            RoundedRectangle(cornerRadius: r + 2 * s, style: .continuous)
                .fill(Color.primary.opacity(0.09))
                .frame(maxWidth: layout.addressBar == .unified ? 120 * s : .infinity)
                .frame(height: min(height - 6 * s, 12 * s))
        case .tabStrip:
            HStack(spacing: layout.tabStyle == .chrome ? 0 : 3 * s) {
                ForEach(0..<3, id: \.self) { index in
                    tabShape(index == 0, s: s, accent: accent, height: height)
                }
                if layout.tabStyle != .safari { Spacer(minLength: 0) }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: layout.tabStyle == .chrome ? .bottom : .center)
        case .flexibleSpace:
            Spacer(minLength: 2 * s)
        default:
            Circle().fill(ink).frame(width: 6 * s, height: 6 * s)
        }
    }

    @ViewBuilder
    private func tabShape(_ selected: Bool, s: CGFloat, accent: Color, height: CGFloat) -> some View {
        switch layout.tabStyle {
        case .chrome:
            ChromeTabShape(radius: 3 * s)
                .fill(selected ? ShellColors.surface : Color.clear)
                .overlay(Capsule().fill(Color.primary.opacity(0.25)).frame(width: 22 * s, height: 2.5 * s))
                .frame(width: 44 * s, height: height - 5 * s)
        case .opera:
            RoundedRectangle(cornerRadius: 3 * s, style: .continuous)
                .fill(selected ? Color.primary.opacity(0.16) : Color.primary.opacity(0.06))
                .overlay(alignment: .bottom) {
                    if selected { Capsule().fill(accent).frame(width: 20 * s, height: 1.5 * s) }
                }
                .frame(width: 40 * s, height: height - 8 * s)
        case .safari:
            RoundedRectangle(cornerRadius: 3 * s, style: .continuous)
                .fill(selected ? Color.primary.opacity(0.14) : Color.primary.opacity(0.05))
                .frame(maxWidth: .infinity)
                .frame(height: height - 5 * s)
        }
    }

    private func sidebar(s: CGFloat, accent: Color, ink: Color, width: CGFloat, lights: Bool) -> some View {
        VStack(alignment: .leading, spacing: 4 * s) {
            HStack {
                if lights { lightsView(s: s) }
                Spacer()
                HStack(spacing: 3 * s) { ForEach(0..<3, id: \.self) { _ in Circle().fill(ink).frame(width: 5 * s) } }
            }
            .frame(height: 14 * s)
            if layout.region(of: .addressField)?.isSidebar == true {
                RoundedRectangle(cornerRadius: 3 * s, style: .continuous)
                    .fill(Color.primary.opacity(0.09)).frame(height: 11 * s)
            }
            HStack(spacing: 3 * s) {
                ForEach(0..<3, id: \.self) { _ in
                    RoundedRectangle(cornerRadius: 3 * s).fill(Color.primary.opacity(0.08)).frame(height: 12 * s)
                }
            }
            ForEach(0..<5, id: \.self) { index in
                HStack(spacing: 4 * s) {
                    RoundedRectangle(cornerRadius: 1.5 * s).fill(index == 0 ? accent : ink).frame(width: 6 * s, height: 6 * s)
                    Capsule().fill(Color.primary.opacity(0.2)).frame(width: CGFloat(40 - index * 5) * s, height: 3 * s)
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 4 * s)
                .frame(height: 11 * s)
                .background(RoundedRectangle(cornerRadius: 3 * s)
                    .fill(index == 0 ? ShellColors.sidebarSelection : .clear))
            }
            Spacer()
        }
        .padding(6 * s)
        .frame(width: width)
    }

    private func page(s: CGFloat, accent: Color) -> some View {
        ZStack(alignment: .topLeading) {
            ShellColors.page
            VStack(alignment: .leading, spacing: 5 * s) {
                Capsule().fill(accent.opacity(0.55)).frame(width: 70 * s, height: 6 * s)
                ForEach(0..<4, id: \.self) { index in
                    Capsule().fill(Color.primary.opacity(0.1)).frame(width: CGFloat(150 - index * 22) * s, height: 3.5 * s)
                }
                HStack(spacing: 6 * s) {
                    ForEach(0..<3, id: \.self) { _ in
                        RoundedRectangle(cornerRadius: 3 * s).fill(Color.primary.opacity(0.06)).frame(width: 46 * s, height: 30 * s)
                    }
                }
                .padding(.top, 4 * s)
            }
            .padding(12 * s)
        }
    }
}
