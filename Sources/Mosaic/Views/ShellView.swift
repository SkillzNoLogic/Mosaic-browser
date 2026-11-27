import AppKit
import MosaicCore
import SwiftUI

/// The whole window, composed from the active Mosaic (`MosaicLayout`).
///
/// The window itself is transparent. The background is painted everywhere except inside the
/// page card, because for Gecko tabs that area is a real hole through which the docked
/// Gecko window shows (see `GeckoHole`). The card's corners are painted over the Gecko
/// window, which is how a Gecko page gets rounded corners too.
struct ShellView: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion
    @Environment(\.accessibilityReduceTransparency) private var systemReduceTransparency
    @State private var relay: ContentRectRelay
    @State private var addressState = AddressBarState()

    init(model: BrowserModel) {
        _relay = State(initialValue: ContentRectRelay { [weak model] rect in
            ShellDiagnostics.contentRect = rect
            model?.setContentRect(rect)
        })
    }

    var body: some View {
        let layout = model.mosaics.effectiveLayout
        let settings = model.shellSettings
        let reduceTransparency = settings.reduceTransparency.resolve(system: systemReduceTransparency)
        let theme = ShellTheme(
            layout: layout,
            accent: Color(hex: layout.appearance.accent),
            glassEnabled: layout.appearance.material == .liquidGlass && !reduceTransparency,
            reduceMotion: settings.reduceMotion.resolve(system: systemReduceMotion),
            prominentFocusRings: settings.prominentFocusRings,
            alwaysShowTabCloseButtons: settings.alwaysShowTabCloseButtons)
        let metrics = ShellMetrics(scale: settings.textSize.scale, density: layout.appearance.density)
        let plan = ShellPlan(layout: layout, model: model, metrics: metrics)

        ShellBody(plan: plan, relay: relay)
            .environment(\.shellTheme, theme)
            .environment(\.shellMetrics, metrics)
            .environment(addressState)
            .tint(theme.accent)
            .backgroundPreferenceValue(ContentCardAnchorKey.self) { anchor in
                GeometryReader { proxy in
                    BackgroundFill(spec: layout.appearance.background)
                        .mask {
                            FrameWithHole(hole: anchor.map { proxy[$0] }, radius: layout.appearance.contentCornerRadius)
                                .fill(style: FillStyle(eoFill: true))
                        }
                }
            }
            .overlayPreferenceValue(AddressFieldAnchorKey.self) { anchor in
                GeometryReader { proxy in
                    if let anchor, addressState.showsSuggestions {
                        let field = proxy[anchor]
                        AddressSuggestionsList()
                            .frame(width: max(field.width, 360))
                            .offset(x: min(field.minX, proxy.size.width - max(field.width, 360) - 8),
                                    y: field.maxY + 6)
                    }
                }
                .environment(\.shellTheme, theme)
                .environment(\.shellMetrics, metrics)
                .environment(addressState)
            }
            .overlay(alignment: plan.sidebarEdge == .trailing ? .trailing : .leading) {
                if plan.sidebarAutoHides {
                    SidebarPeek(edge: plan.sidebarEdge, width: plan.sidebarWidth)
                        .environment(\.shellTheme, theme)
                        .environment(\.shellMetrics, metrics)
                        .environment(addressState)
                }
            }
            // Tabs area's overlays (tab search, "already open", crash recovery).
            .overlay { ShellTabsOverlayMount() }
            // Productivity area's ⌘K palette, over the whole window.
            .overlay { CommandPaletteOverlay() }
            .overlay(alignment: .bottom) { NoticeBanner().environment(\.shellTheme, theme) }
            .background {
                WindowConfigurator(colorScheme: layout.appearance.colorScheme, firstRowHeight: plan.firstRowHeight)
            }
            .ignoresSafeArea()
            .animation(theme.animation(.smooth(duration: 0.25)), value: layout)
            .animation(theme.animation(), value: model.isSidebarVisible)
            .onChange(of: model.showExtensions) { _, show in
                // The extensions sheet is gone: Settings › Extensions replaces it.
                guard show else { return }
                model.showExtensions = false
                model.showSettings(.extensions)
            }
    }
}

/// Values for diagnostics (`dump`).
@MainActor
enum ShellDiagnostics {
    /// The page card's rectangle on screen (AppKit coordinates), as last reported.
    static var contentRect: CGRect?
}

/// Where the page card is, for the background's hole.
struct ContentCardAnchorKey: PreferenceKey {
    static let defaultValue: Anchor<CGRect>? = nil
    static func reduce(value: inout Anchor<CGRect>?, nextValue: () -> Anchor<CGRect>?) {
        value = value ?? nextValue()
    }
}

/// The full rect minus a rounded hole (filled even-odd).
private struct FrameWithHole: Shape {
    let hole: CGRect?
    let radius: CGFloat

    func path(in rect: CGRect) -> Path {
        var path = Path(rect)
        if let hole, hole.width > 0, hole.height > 0 {
            path.addRoundedRect(in: hole, cornerSize: CGSize(width: radius, height: radius), style: .continuous)
        }
        return path
    }
}

// MARK: - Plan

/// Decisions about which rows exist and where the traffic lights go, made once per render.
struct ShellPlan {
    let layout: MosaicLayout
    let titleItems: [RegionItem]
    let toolbarItems: [RegionItem]
    let tabBarItems: [RegionItem]
    let railItems: [RegionItem]
    /// Items for the strip shown above the page when a vertical sidebar is collapsed.
    let collapsedStripItems: [RegionItem]
    let sidebarVisible: Bool
    let sidebarAutoHides: Bool
    let sidebarEdge: HorizontalEdge
    let showsTitleStrip: Bool
    let metrics: ShellMetrics

    @MainActor
    init(layout: MosaicLayout, model: BrowserModel, metrics: ShellMetrics) {
        self.layout = layout
        self.metrics = metrics
        let home = model.shellSettings.showsHomeButton
        titleItems = layout.drawnItems(in: .titleBar, showsHome: home)
        toolbarItems = layout.drawnItems(in: .toolbar, showsHome: home)
        tabBarItems = layout.drawnItems(in: .tabBar, showsHome: home)
        railItems = layout.drawnItems(in: .rail, showsHome: home)
        sidebarVisible = model.isSidebarVisible
        sidebarAutoHides = layout.tabPlacement.isVertical && layout.sidebar == .autoHide
        sidebarEdge = layout.tabPlacement == .sidebarTrailing ? .trailing : .leading

        // A hidden vertical sidebar would take the address field and navigation with it:
        // show its top and middle rows in a strip above the page instead.
        let sidebarHidden = layout.tabPlacement.isVertical && !sidebarVisible
        if sidebarHidden, layout.region(of: .addressField)?.isSidebar == true {
            var items = layout.drawnItems(in: .sidebarTop, showsHome: home).filter { $0.component != .flexibleSpace }
            let middle = layout.drawnItems(in: .sidebarMiddle, showsHome: home)
            items += middle.map { RegionItem(id: $0.id + 1000, component: $0.component,
                                             attachesNewTab: $0.attachesNewTab, embedsStar: $0.embedsStar) }
            collapsedStripItems = items
        } else {
            collapsedStripItems = []
        }
        let leadingSidebarHoldsLights = sidebarVisible && sidebarEdge == .leading
        showsTitleStrip = titleItems.isEmpty && !leadingSidebarHoldsLights
            && (toolbarItems.isEmpty || !collapsedStripItems.isEmpty)
    }

    var titleRowHeight: CGFloat {
        if titleItems.contains(where: { $0.component == .addressField }) && layout.addressBar == .unified {
            return metrics.unifiedRowHeight
        }
        if titleItems.contains(where: { $0.component == .tabStrip }) { return metrics.tabRowHeight }
        return metrics.rowHeight
    }

    var tabBarHeight: CGFloat {
        tabBarItems.contains { $0.component == .tabStrip } ? metrics.segmentedTabBarHeight : metrics.rowHeight
    }

    var railWidth: CGFloat { railItems.isEmpty ? 0 : metrics.railWidth }

    /// The sidebar grows with the Accessibility interface size so its controls still fit.
    var sidebarWidth: CGFloat { layout.sidebarWidth * metrics.scale }

    /// Height of the row holding the traffic lights.
    var firstRowHeight: CGFloat {
        if !titleItems.isEmpty { return titleRowHeight }
        if sidebarVisible && sidebarEdge == .leading { return metrics.tabRowHeight }
        if showsTitleStrip { return collapsedStripItems.isEmpty ? metrics.titleStripHeight : metrics.rowHeight }
        return metrics.rowHeight
    }

    /// Leading space the first row must leave for the traffic lights.
    func lightsPad(startingAt x: CGFloat) -> CGFloat { max(0, metrics.trafficLightsWidth - x) }
}

// MARK: - Body

private struct ShellBody: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    let plan: ShellPlan
    let relay: ContentRectRelay

    var body: some View {
        VStack(spacing: 0) {
            if !plan.titleItems.isEmpty {
                titleRow
            }
            HStack(spacing: 0) {
                if !plan.railItems.isEmpty {
                    RailView(topPadding: plan.titleItems.isEmpty ? plan.firstRowHeight : 0)
                }
                if plan.sidebarVisible, plan.sidebarEdge == .leading {
                    sidebar(holdsLights: plan.titleItems.isEmpty)
                    sidebarDivider
                }
                mainColumn
                if plan.sidebarVisible, plan.sidebarEdge == .trailing {
                    sidebarDivider
                    sidebar(holdsLights: false)
                }
            }
        }
        .frame(minWidth: 0, maxWidth: .infinity, minHeight: 0, maxHeight: .infinity)
        // Light/dark comes from the window's appearance, set by WindowConfigurator.
    }

    // MARK: Rows

    private var titleRow: some View {
        let isTabRow = plan.titleItems.contains { $0.component == .tabStrip } && plan.layout.tabStyle == .chrome
        return HStack(spacing: 0) {
            WindowDragArea().frame(width: plan.lightsPad(startingAt: 0))
            RegionRow(region: .titleBar, items: plan.titleItems, spacing: 4)
                .padding(.trailing, 8)
                .padding(.leading, isTabRow ? 0 : 6)
        }
        .frame(height: plan.titleRowHeight, alignment: isTabRow ? .bottom : .center)
        .background {
            if plan.layout.appearance.toolbarSurface && plan.layout.appearance.background.kind == .system {
                ShellColors.frame
            }
        }
        .zIndex(1)
    }

    private var mainColumn: some View {
        let layout = plan.layout
        let inset = layout.appearance.contentInset
        let leadingX = plan.railWidth + (plan.sidebarVisible && plan.sidebarEdge == .leading ? plan.sidebarWidth : 0)
        let firstRowIsHere = plan.titleItems.isEmpty && !(plan.sidebarVisible && plan.sidebarEdge == .leading)
        let hasRowAbove = !plan.titleItems.isEmpty || plan.showsTitleStrip || !plan.toolbarItems.isEmpty
            || !plan.tabBarItems.isEmpty
        return VStack(spacing: 0) {
            if plan.showsTitleStrip {
                HStack(spacing: 4) {
                    WindowDragArea().frame(width: plan.lightsPad(startingAt: leadingX) + 4)
                    if plan.collapsedStripItems.isEmpty {
                        WindowDragArea()
                    } else {
                        RegionRow(region: .sidebarMiddle, items: plan.collapsedStripItems, spacing: 4)
                            .frame(maxWidth: 900)
                        WindowDragArea().frame(minWidth: 8, maxWidth: .infinity)
                    }
                }
                .padding(.trailing, 8)
                .frame(height: plan.firstRowHeight)
            }
            if !plan.toolbarItems.isEmpty {
                HStack(spacing: 0) {
                    if firstRowIsHere && !plan.showsTitleStrip {
                        WindowDragArea().frame(width: plan.lightsPad(startingAt: leadingX))
                    }
                    RegionRow(region: .toolbar, items: plan.toolbarItems, spacing: 4)
                }
                .padding(.horizontal, 8)
                .frame(height: metrics.rowHeight)
                .background { if layout.appearance.toolbarSurface { ShellColors.surface } }
                .zIndex(1)
            }
            if !plan.tabBarItems.isEmpty {
                RegionRow(region: .tabBar, items: plan.tabBarItems, spacing: 4)
                    .padding(.horizontal, 8)
                    .padding(.bottom, 4)
                    .frame(height: plan.tabBarHeight)
            }
            if layout.bookmarksBar {
                // Library area's bookmarks bar: full width, its own height
                // (`BookmarksBarView.height`), nothing at all while the user has hidden it (⇧⌘B).
                BookmarksBarView()
                    .frame(maxWidth: .infinity)
                    .background { if layout.appearance.toolbarSurface { ShellColors.surface } }
            }
            if inset == 0, hasRowAbove || layout.bookmarksBar {
                Rectangle().fill(Color.primary.opacity(0.1)).frame(height: 1)
            }
            ContentCard(relay: relay)
                .anchorPreference(key: ContentCardAnchorKey.self, value: .bounds) { $0 }
                .padding(.horizontal, inset)
                .padding(.bottom, inset)
                .padding(.top, hasRowAbove || layout.bookmarksBar ? (inset > 0 ? max(2, inset / 2) : 0) : inset)
        }
    }

    private func sidebar(holdsLights: Bool) -> some View {
        SidebarView(topLeadingPad: holdsLights ? plan.lightsPad(startingAt: plan.railWidth + 8) : 0,
                    topRowHeight: holdsLights ? plan.firstRowHeight : metrics.tabRowHeight)
            .frame(width: plan.sidebarWidth)
            .transition(.move(edge: plan.sidebarEdge == .leading ? .leading : .trailing).combined(with: .opacity))
    }

    @ViewBuilder private var sidebarDivider: some View {
        if plan.layout.appearance.contentInset == 0 {
            Rectangle().fill(Color.primary.opacity(0.1)).frame(width: 1)
        }
    }
}

// MARK: - Auto-hiding sidebar

/// Hidden until the pointer reaches the window's edge (or ⌘⇧L), then shown over the page on
/// an opaque panel, since it often covers a Gecko page, which is a separate window.
private struct SidebarPeek: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    let edge: HorizontalEdge
    let width: CGFloat

    var body: some View {
        let peeking = model.shellSettings.sidebarPeek
        ZStack(alignment: edge == .leading ? .leading : .trailing) {
            // Nearly transparent rather than clear: fully transparent pixels let clicks and
            // hovers through to the window behind.
            Color.black.opacity(0.01)
                .frame(width: 8)
                .padding(.top, metrics.titleStripHeight)
                .onHover { if $0 { model.shellSettings.sidebarPeek = true } }
                .accessibilityHidden(true)
            if peeking {
                SidebarView(topLeadingPad: edge == .leading ? metrics.trafficLightsWidth - 8 : 0,
                            topRowHeight: metrics.titleStripHeight)
                    .frame(width: width)
                    .padding(.vertical, 6)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(ShellColors.overlayPanel))
                    .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                    .shadow(color: .black.opacity(0.25), radius: 18, y: 4)
                    .padding(6)
                    .onHover { if !$0 { model.shellSettings.sidebarPeek = false } }
                    .transition(.move(edge: edge == .leading ? .leading : .trailing).combined(with: .opacity))
            }
        }
        .frame(maxHeight: .infinity)
        .animation(theme.animation(), value: peeking)
    }
}

// MARK: - Notice

/// Short-lived message at the bottom of the window. Opaque: it usually lies over the page.
private struct NoticeBanner: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        if let notice = model.notice {
            Text(notice)
                .font(.callout)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .background(Capsule().fill(ShellColors.overlayPanel))
                .overlay(Capsule().strokeBorder(Color.primary.opacity(0.12)))
                .shadow(color: .black.opacity(0.15), radius: 8, y: 2)
                .padding(.bottom, 22)
                .transition(.opacity)
                .accessibilityAddTraits(.updatesFrequently)
                .task(id: notice) {
                    try? await Task.sleep(nanoseconds: 3_500_000_000)
                    if model.notice == notice { model.notice = nil }
                }
        }
    }
}
