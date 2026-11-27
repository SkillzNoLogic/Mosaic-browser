import MosaicCore
import SwiftUI

/// The sidebar: its top and middle rows (Arc: navigation and the address field), the tab
/// list with pinned favourites, and its bottom row (workspaces, new tab…).
struct SidebarView: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    /// Space to leave at the start of the top row for the window's traffic lights.
    var topLeadingPad: CGFloat = 0
    /// Height of the top row (matches the window's first row when this holds the lights).
    var topRowHeight: CGFloat

    var body: some View {
        let layout = theme.layout
        let showsHome = model.shellSettings.showsHomeButton
        let top = layout.drawnItems(in: .sidebarTop, showsHome: showsHome)
        let middle = layout.drawnItems(in: .sidebarMiddle, showsHome: showsHome)
        let bottom = layout.drawnItems(in: .sidebarBottom, showsHome: showsHome)
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 0) {
                if topLeadingPad > 0 { WindowDragArea().frame(width: topLeadingPad) }
                if top.isEmpty {
                    WindowDragArea()
                } else {
                    RegionRow(region: .sidebarTop, items: top, spacing: 2)
                }
            }
            .frame(height: topRowHeight)
            if !middle.isEmpty {
                RegionRow(region: .sidebarMiddle, items: middle, spacing: 4)
                    .padding(.bottom, 6)
            }
            VerticalTabList()
            if !bottom.isEmpty {
                RegionRow(region: .sidebarBottom, items: bottom, spacing: 2)
                    .frame(height: metrics.rowHeight - 4)
            }
        }
        .padding(.horizontal, 8)
        .padding(.bottom, 4)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Sidebar")
    }
}

/// Opera's quick-access rail.
struct RailView: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    var topPadding: CGFloat

    var body: some View {
        let items = theme.layout.drawnItems(in: .rail, showsHome: model.shellSettings.showsHomeButton)
        RegionColumn(region: .rail, items: items)
            .padding(.top, topPadding + 6)
            .padding(.bottom, 10)
            .frame(width: metrics.railWidth)
            .frame(maxHeight: .infinity)
            .background(alignment: .trailing) {
                if theme.appearance.contentInset == 0 {
                    Rectangle().fill(Color.primary.opacity(0.08)).frame(width: 1)
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Quick access")
    }
}
