import AppKit
import MosaicCore
import SwiftUI

// Tabs: the horizontal strip in Chrome, Opera and Safari styles, and the vertical list
// (pinned favourites, groups, tabs) used by sidebars. Behaviour (pinning, groups, moving)
// belongs to the tabs area; these views draw the model's state and call its methods.

// MARK: - Site icon

/// A monogram for the page's site (Mosaic has no favicon service yet), with the engine's
/// colour as a small dot and a spinner while loading.
struct SiteIcon: View {
    @ObservedObject var tab: BrowserTab
    var size: CGFloat = 16

    var body: some View {
        ZStack {
            if let page = tab.internalPage {
                Image(systemName: Self.symbol(for: page))
                    .font(.system(size: size * 0.75, weight: .medium))
                    .foregroundStyle(.secondary)
            } else if tab.isStartPage {
                Image(systemName: "square.grid.2x2")
                    .font(.system(size: size * 0.72, weight: .medium))
                    .foregroundStyle(.secondary)
            } else {
                RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                    .fill(Self.color(for: host))
                Text(monogram)
                    .font(.system(size: size * 0.6, weight: .bold, design: .rounded))
                    .foregroundStyle(.white)
            }
            if tab.isLoading {
                ProgressView().controlSize(.mini).scaleEffect(size / 16)
                    .frame(width: size, height: size)
                    .background(Circle().fill(Color(nsColor: .windowBackgroundColor)).padding(-1))
            }
        }
        .frame(width: size, height: size)
        .opacity(tab.shellIsAsleep ? BrowserTab.shellAsleepOpacity : 1)
        .overlay(alignment: .bottomTrailing) {
            if !tab.isStartPage, tab.internalPage == nil {
                Circle()
                    .fill(ShellColors.tint(for: tab.engine))
                    .overlay(Circle().strokeBorder(Color(nsColor: .windowBackgroundColor), lineWidth: 1.2))
                    .frame(width: size * 0.48, height: size * 0.48)
                    .offset(x: size * 0.18, y: size * 0.18)
            }
        }
        .accessibilityHidden(true)
    }

    private var host: String {
        let host = URL(string: tab.url)?.host ?? tab.url
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }

    private var monogram: String {
        String((tab.title.isEmpty ? host : host).prefix(1)).uppercased()
    }

    static func color(for host: String) -> Color {
        // Stable across launches (unlike `hashValue`).
        var hash: UInt32 = 5381
        for byte in host.utf8 { hash = (hash &* 33) &+ UInt32(byte) }
        let hue = Double(hash % 360) / 360
        return Color(hue: hue, saturation: 0.55, brightness: 0.72)
    }

    static func symbol(for page: InternalPage) -> String {
        switch page {
        case .bookmarks: "book"
        case .history: "clock"
        case .downloads: "arrow.down.circle"
        case .readingList: "eyeglasses"
        }
    }
}

// MARK: - Tab context menu

/// Actions every tab offers, built from existing model methods.
struct TabContextMenu: View {
    @EnvironmentObject private var model: BrowserModel
    let tab: BrowserTab

    var body: some View {
        Button("New Tab") { model.openNewTabFromShell() }
        Divider()
        Button("Reload") {
            model.select(tab)
            model.reload()
        }
        .disabled(tab.isStartPage || tab.internalPage != nil)
        Button("Duplicate") { model.shellDuplicate(tab) }
            .disabled(tab.isStartPage)
        if TabsBridge.tabsBehaviourAvailable {
            Button(tab.isPinned ? "Unpin Tab" : "Pin Tab") { model.shellTogglePin(tab) }
            Button(tab.isMuted ? "Unmute Tab" : "Mute Tab") { model.shellToggleMute(tab) }
        }
        Button("Open in \(tab.engine.other.displayName)") {
            model.select(tab)
            model.setEngine(tab.engine.other)
        }
        .disabled(tab.internalPage != nil || (tab.engine == .gecko && !model.chromiumAvailable))
        Button("Copy Link") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(tab.url, forType: .string)
        }
        .disabled(tab.isStartPage)
        Divider()
        Button("Close Tab") { model.close(tab) }
        Button("Close Other Tabs") {
            for other in model.displayedTabs where other !== tab && !other.isPinned { model.close(other) }
        }
        .disabled(model.displayedTabs.count < 2)
    }
}

// MARK: - Horizontal strip

/// Tabs in a row. `newTabAttached` draws the new-tab button right after the last tab
/// (Chrome, Opera); Safari's segmented style fills the whole width instead.
struct HorizontalTabStrip: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    let style: TabStyle
    var newTabAttached: Bool

    private let pinnedWidth: CGFloat = 44

    var body: some View {
        GeometryReader { proxy in
            let tabs = model.displayedTabs
            let pinned = tabs.filter(\.isPinned)
            let regular = tabs.filter { !$0.isPinned }
            let addWidth: CGFloat = newTabAttached ? metrics.button + 8 : 0
            let spacing: CGFloat = style == .opera ? 6 : (style == .safari ? 4 : 0)
            let available = proxy.size.width - addWidth - CGFloat(pinned.count) * (pinnedWidth + spacing)
                - CGFloat(max(regular.count - 1, 0)) * spacing - (style == .chrome ? 16 : 4)
            let ideal = regular.isEmpty ? 0 : available / CGFloat(regular.count)
            let maxWidth: CGFloat = style == .safari ? 10_000 : 240
            let minWidth: CGFloat = style == .safari ? 90 : 72
            let width = min(maxWidth, max(minWidth, ideal))
            let overflow = ideal < minWidth

            ScrollViewReader { scroller in
                ScrollView(.horizontal) {
                    HStack(alignment: style == .chrome ? .bottom : .center, spacing: spacing) {
                        ForEach(pinned) { tab in
                            HorizontalTab(tab: tab, style: style, width: pinnedWidth, iconOnly: true,
                                          isSelected: tab.id == model.selectedTabID)
                                .id(tab.id)
                        }
                        ForEach(Array(regular.enumerated()), id: \.element.id) { index, tab in
                            HorizontalTab(tab: tab, style: style, width: width, iconOnly: false,
                                          isSelected: tab.id == model.selectedTabID,
                                          showsSeparator: style == .chrome && index < regular.count - 1
                                              && tab.id != model.selectedTabID
                                              && regular[index + 1].id != model.selectedTabID)
                                .id(tab.id)
                        }
                        if newTabAttached {
                            NewTabButton()
                                .padding(.leading, style == .chrome ? 6 : 2)
                                .frame(height: proxy.size.height, alignment: .center)
                        }
                    }
                    .padding(.horizontal, style == .chrome ? 8 : 2)
                    .frame(minWidth: overflow ? nil : proxy.size.width, maxHeight: .infinity,
                           alignment: style == .chrome ? .bottomLeading : .leading)
                }
                .scrollIndicators(.never)
                .scrollDisabled(!overflow)
                .onChange(of: model.selectedTabID) { _, id in
                    guard overflow, let id else { return }
                    withAnimation(theme.animation()) { scroller.scrollTo(id) }
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Tabs")
    }
}

private struct HorizontalTab: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    @ObservedObject var tab: BrowserTab
    let style: TabStyle
    let width: CGFloat
    let iconOnly: Bool
    let isSelected: Bool
    var showsSeparator = false
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 7) {
            if style == .safari, !iconOnly { closeButton.opacity(showsClose ? 1 : 0) }
            if style == .safari, !iconOnly { Spacer(minLength: 0) }
            SiteIcon(tab: tab, size: 15 * metrics.scale)
            if !iconOnly {
                Text(tab.displayTitle)
                    .font(.system(size: metrics.smallFont + 0.5, weight: isSelected ? .medium : .regular))
                    .opacity(tab.shellIsAsleep ? BrowserTab.shellAsleepOpacity : 1)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: style == .safari ? nil : .infinity, alignment: .leading)
                    .layoutPriority(-1)
                if tab.isMuted {
                    Image(systemName: "speaker.slash.fill").font(.system(size: 9)).foregroundStyle(.secondary)
                        .accessibilityLabel("Muted")
                }
                if style == .safari {
                    Spacer(minLength: 0)
                    Color.clear.frame(width: 16, height: 16)
                } else {
                    closeButton.opacity(showsClose ? 1 : 0)
                }
            }
        }
        .padding(.horizontal, iconOnly ? 0 : 10)
        .frame(width: width, height: height)
        .modifier(TabSurface(style: style, isSelected: isSelected, hovering: hovering))
        .overlay(alignment: .trailing) {
            if showsSeparator {
                Rectangle().fill(Color.primary.opacity(0.18)).frame(width: 1, height: height * 0.5)
                    .offset(x: 0.5)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { model.select(tab) }
        .onHover { hovering = $0 }
        .help(tab.isStartPage ? "New Tab" : "\(tab.displayTitle)\n\(tab.url)")
        .contextMenu { TabContextMenu(tab: tab) }
        .tabDragAndDrop(tab)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(tab.displayTitle), \(tab.engine.displayName)\(tab.shellIsAsleep ? ", asleep" : "")")
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
        .accessibilityAction(named: "Close Tab") { model.close(tab) }
        .accessibilityAction { model.select(tab) }
    }

    private var height: CGFloat {
        switch style {
        case .chrome: metrics.tabHeight + 2
        case .opera: metrics.tabHeight - 2
        case .safari: metrics.segmentedTabBarHeight - 6
        }
    }

    private var showsClose: Bool { isSelected || hovering || theme.alwaysShowTabCloseButtons }

    private var closeButton: some View {
        Button {
            model.close(tab)
        } label: {
            Image(systemName: "xmark")
                .font(.system(size: 9, weight: .bold))
                .frame(width: 18, height: 18)
                .contentShape(Rectangle())
        }
        .buttonStyle(ToolbarButtonStyle(width: 18, height: 18))
        .help("Close Tab (⌘W)")
        .accessibilityLabel("Close \(tab.displayTitle)")
    }
}

/// The selected/hovered look of a horizontal tab. Glass is applied to the tab itself (not to
/// a background view) so that, inside a glass container, it is drawn behind the title.
private struct TabSurface: ViewModifier {
    @Environment(\.shellTheme) private var theme
    let style: TabStyle
    let isSelected: Bool
    let hovering: Bool

    func body(content: Content) -> some View {
        switch style {
        case .chrome:
            content.background {
                if isSelected {
                    ChromeTabShape(radius: min(10, max(6, theme.cornerRadius)))
                        .fill(theme.appearance.toolbarSurface ? ShellColors.surface : ShellColors.sidebarSelection)
                        .shadow(color: .black.opacity(0.08), radius: 1.5, y: -0.5)
                } else if hovering {
                    RoundedRectangle(cornerRadius: theme.cornerRadius, style: .continuous)
                        .fill(Color.primary.opacity(0.07))
                        .padding(.vertical, 3)
                        .padding(.horizontal, 2)
                }
            }
        case .opera, .safari:
            let radius = style == .opera ? theme.cornerRadius : max(5, theme.cornerRadius - 2)
            let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
            if isSelected {
                content
                    .chromeSurface(shape, role: .selected,
                                   tint: theme.appearance.glow ? theme.accent.opacity(0.35) : nil)
                    .overlay(alignment: .bottom) {
                        if style == .opera {
                            Capsule().fill(theme.accent).frame(height: 2).padding(.horizontal, 12)
                        }
                    }
                    .shadow(color: .black.opacity(theme.glassEnabled ? 0 : 0.1), radius: 1.5, y: 0.5)
            } else {
                content.background(shape.fill(Color.primary.opacity(hovering ? 0.1 : (style == .opera ? 0.05 : 0.045))))
            }
        }
    }
}

/// Selected / hovered look of a sidebar row or pinned tile (glass on the row itself).
struct RowSurface: ViewModifier {
    @Environment(\.shellTheme) private var theme
    let isSelected: Bool
    let hovering: Bool
    var cornerRadius: CGFloat? = nil
    var restingOpacity: Double = 0

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius ?? theme.cornerRadius, style: .continuous)
        if isSelected {
            content
                .chromeSurface(shape, role: .selected)
                .shadow(color: .black.opacity(theme.glassEnabled ? 0 : 0.07), radius: 1.5, y: 0.5)
        } else {
            content.background(shape.fill(Color.primary.opacity(hovering ? restingOpacity + 0.07 : restingOpacity)))
        }
    }
}

/// Chrome's tab: rounded top corners and outward curves at the bottom, so the selected tab
/// flows into the toolbar below it.
struct ChromeTabShape: Shape {
    var radius: CGFloat = 8

    func path(in rect: CGRect) -> Path {
        let r = min(radius, rect.height / 2, rect.width / 4)
        var p = Path()
        p.move(to: CGPoint(x: rect.minX - r, y: rect.maxY))
        p.addArc(tangent1End: CGPoint(x: rect.minX, y: rect.maxY), tangent2End: CGPoint(x: rect.minX, y: rect.maxY - r), radius: r)
        p.addLine(to: CGPoint(x: rect.minX, y: rect.minY + r))
        p.addArc(tangent1End: CGPoint(x: rect.minX, y: rect.minY), tangent2End: CGPoint(x: rect.minX + r, y: rect.minY), radius: r)
        p.addLine(to: CGPoint(x: rect.maxX - r, y: rect.minY))
        p.addArc(tangent1End: CGPoint(x: rect.maxX, y: rect.minY), tangent2End: CGPoint(x: rect.maxX, y: rect.minY + r), radius: r)
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY - r))
        p.addArc(tangent1End: CGPoint(x: rect.maxX, y: rect.maxY), tangent2End: CGPoint(x: rect.maxX + r, y: rect.maxY), radius: r)
        p.closeSubpath()
        return p
    }
}

struct NewTabButton: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        Button {
            model.openNewTabFromShell()
        } label: {
            Image(systemName: "plus")
        }
        .buttonStyle(ToolbarButtonStyle())
        .help("New Tab (⌘T)")
        .accessibilityLabel("New Tab")
    }
}

// MARK: - Vertical list

/// The sidebar's tabs: pinned favourites as a grid, then groups and tabs as rows, then a
/// New Tab row.
struct VerticalTabList: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics

    var body: some View {
        let pinned = model.displayedPinnedTabs
        let regular = model.displayedUnpinnedTabs
        ScrollView {
            VStack(alignment: .leading, spacing: 2) {
                if !pinned.isEmpty {
                    PinnedGrid(tabs: pinned)
                        .padding(.bottom, 8)
                }
                Text(currentWorkspaceName)
                    .font(.system(size: metrics.smallFont, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 10)
                    .padding(.bottom, 2)
                    .accessibilityAddTraits(.isHeader)
                NewTabRow()
                ForEach(rows(regular)) { row in
                    switch row.kind {
                    case .group(let group):
                        GroupHeaderRow(group: group)
                    case .tab(let tab):
                        VerticalTabRow(tab: tab, isSelected: tab.id == model.selectedTabID,
                                       groupColor: group(of: tab)?.color.shellSwatch)
                    }
                }
            }
            .padding(.vertical, 4)
            .animation(theme.animation(), value: regular.map(\.id))
        }
        .scrollIndicators(.never)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Tabs")
    }

    private var currentWorkspaceName: String {
        model.workspaces.first { $0.id == model.selectedWorkspaceID }?.name ?? "Tabs"
    }

    private func group(of tab: BrowserTab) -> TabGroup? {
        guard let id = tab.groupID else { return nil }
        return model.groups.first { $0.id == id }
    }

    private struct Row: Identifiable {
        enum Kind { case group(TabGroup), tab(BrowserTab) }
        let id: String
        let kind: Kind
    }

    /// Tabs in order, with a header before the first tab of each group; tabs of a collapsed
    /// group are left out.
    private func rows(_ tabs: [BrowserTab]) -> [Row] {
        var rows: [Row] = []
        var lastGroup: UUID?
        for tab in tabs {
            if let group = group(of: tab) {
                if group.id != lastGroup {
                    rows.append(Row(id: "g-\(group.id)-\(tab.id)", kind: .group(group)))
                }
                lastGroup = group.id
                if group.isCollapsed { continue }
            } else {
                lastGroup = nil
            }
            rows.append(Row(id: "t-\(tab.id)", kind: .tab(tab)))
        }
        return rows
    }
}

private struct NewTabRow: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellMetrics) private var metrics

    var body: some View {
        Button {
            model.openNewTabFromShell()
        } label: {
            Label("New Tab", systemImage: "plus")
                .font(.system(size: metrics.font))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 10)
                .frame(height: metrics.sidebarRowHeight)
        }
        .buttonStyle(HoverRowStyle())
        .help("New Tab (⌘T)")
    }
}

private struct GroupHeaderRow: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellMetrics) private var metrics
    let group: TabGroup

    var body: some View {
        let header = HStack(spacing: 6) {
            Image(systemName: group.isCollapsed ? "chevron.right" : "chevron.down")
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(.secondary)
            Circle().fill(group.color.shellSwatch).frame(width: 8, height: 8)
            Text(group.name.isEmpty ? "Unnamed group" : group.name)
                .font(.system(size: metrics.smallFont, weight: .semibold))
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 10)
        .frame(height: metrics.sidebarRowHeight - 6)
        .contentShape(Rectangle())
        if TabsBridge.tabsBehaviourAvailable {
            Button { model.shellToggleGroupCollapsed(group.id) } label: { header }
                .buttonStyle(HoverRowStyle())
                .accessibilityLabel("Group \(group.name)")
                .accessibilityValue(group.isCollapsed ? "Collapsed" : "Expanded")
                .accessibilityHint("Shows or hides the group's tabs")
        } else {
            header
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Group \(group.name)\(group.isCollapsed ? ", collapsed" : "")")
        }
    }
}

/// Drag a tab onto another to move it there (tabs area: `moveTab(_:to:)`). Only offered once
/// the tabs area's reordering is present.
private struct TabDragAndDrop: ViewModifier {
    @EnvironmentObject private var model: BrowserModel
    let tab: BrowserTab

    func body(content: Content) -> some View {
        if TabsBridge.tabsBehaviourAvailable {
            content
                .draggable(tab.id.uuidString)
                .dropDestination(for: String.self) { items, _ in
                    guard let raw = items.first, let id = UUID(uuidString: raw),
                          let dragged = model.tabs.first(where: { $0.id == id }), dragged !== tab,
                          let target = model.displayedTabs.firstIndex(of: tab) else { return false }
                    model.shellMoveTab(dragged, to: target)
                    return true
                }
        } else {
            content
        }
    }
}

extension View {
    fileprivate func tabDragAndDrop(_ tab: BrowserTab) -> some View { modifier(TabDragAndDrop(tab: tab)) }
}

struct VerticalTabRow: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    @ObservedObject var tab: BrowserTab
    let isSelected: Bool
    var groupColor: Color?
    @State private var hovering = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: theme.cornerRadius, style: .continuous)
        HStack(spacing: 9) {
            SiteIcon(tab: tab, size: 16 * metrics.scale)
            Text(tab.displayTitle)
                .font(.system(size: metrics.font, weight: isSelected ? .medium : .regular))
                .opacity(tab.shellIsAsleep ? BrowserTab.shellAsleepOpacity : 1)
                .lineLimit(1)
                .truncationMode(.tail)
            Spacer(minLength: 0)
            if tab.isMuted {
                Image(systemName: "speaker.slash.fill").font(.system(size: 10)).foregroundStyle(.secondary)
                    .accessibilityLabel("Muted")
            }
            Button {
                model.close(tab)
            } label: {
                Image(systemName: "xmark").font(.system(size: 10, weight: .bold))
            }
            .buttonStyle(ToolbarButtonStyle(width: 22, height: 22))
            .opacity(hovering || isSelected || theme.alwaysShowTabCloseButtons ? 1 : 0)
            .help("Close Tab (⌘W)")
            .accessibilityLabel("Close \(tab.displayTitle)")
        }
        .padding(.leading, groupColor == nil ? 10 : 16)
        .padding(.trailing, 6)
        .frame(height: metrics.sidebarRowHeight)
        .modifier(RowSurface(isSelected: isSelected, hovering: hovering))
        .overlay(alignment: .leading) {
            if let groupColor {
                Capsule().fill(groupColor).frame(width: 3).padding(.vertical, 8).padding(.leading, 5)
            }
        }
        .contentShape(shape)
        .onTapGesture { model.select(tab) }
        .onHover { hovering = $0 }
        .help(tab.isStartPage ? "New Tab" : "\(tab.displayTitle)\n\(tab.url)")
        .contextMenu { TabContextMenu(tab: tab) }
        .tabDragAndDrop(tab)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(tab.displayTitle), \(tab.engine.displayName)\(tab.shellIsAsleep ? ", asleep" : "")")
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
        .accessibilityAction(named: "Close Tab") { model.close(tab) }
        .accessibilityAction { model.select(tab) }
    }
}

/// Arc's favourites: pinned tabs as tiles.
private struct PinnedGrid: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    let tabs: [BrowserTab]

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 50, maximum: 80), spacing: 6)], spacing: 6) {
            ForEach(tabs) { tab in
                PinnedTile(tab: tab, isSelected: tab.id == model.selectedTabID)
            }
        }
    }
}

private struct PinnedTile: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @ObservedObject var tab: BrowserTab
    let isSelected: Bool
    @State private var hovering = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: theme.cornerRadius + 2, style: .continuous)
        SiteIcon(tab: tab, size: 20)
            .frame(maxWidth: .infinity)
            .frame(height: 44)
            .modifier(RowSurface(isSelected: isSelected, hovering: hovering, cornerRadius: theme.cornerRadius + 2,
                                 restingOpacity: 0.06))
            .contentShape(shape)
            .onTapGesture { model.select(tab) }
            .onHover { hovering = $0 }
            .help("\(tab.displayTitle)\n\(tab.url)")
            .contextMenu { TabContextMenu(tab: tab) }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Pinned: \(tab.displayTitle)")
            .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
            .accessibilityAction { model.select(tab) }
    }
}
