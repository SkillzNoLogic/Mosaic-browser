import AppKit
import MosaicCore
import SwiftUI

// Draws the components of one region. A region is an ordered list of `ShellComponent`s;
// two pairs attach to each other when adjacent: `.tabStrip` + `.newTab` (the button sits
// right after the last tab) and `.addressField` + `.bookmarkStar` (the star sits inside
// the field).

struct RegionItem: Identifiable {
    let id: Int
    let component: ShellComponent
    var attachesNewTab = false
    var embedsStar = false
}

extension MosaicLayout {
    /// What the shell actually draws for `region`, after the attachment rules and the
    /// components that don't apply (tab strip with vertical tabs, hidden Home button).
    func drawnItems(in region: ShellRegion, showsHome: Bool) -> [RegionItem] {
        let list = components(in: region)
        var items: [RegionItem] = []
        var skipNext = false
        for (index, component) in list.enumerated() {
            if skipNext { skipNext = false; continue }
            let next = index + 1 < list.count ? list[index + 1] : nil
            switch component {
            case .tabStrip where tabPlacement.isVertical: continue
            case .home where !showsHome: continue
            case .sidebarToggle where !hasSidebar: continue
            case .tabStrip:
                let attach = next == .newTab
                items.append(RegionItem(id: index, component: component, attachesNewTab: attach))
                skipNext = attach
            case .addressField:
                let embed = next == .bookmarkStar
                items.append(RegionItem(id: index, component: component, embedsStar: embed))
                skipNext = embed
            default:
                items.append(RegionItem(id: index, component: component))
            }
        }
        // A row of nothing but gaps draws nothing.
        return items.contains { $0.component != .flexibleSpace } ? items : []
    }
}

/// One region drawn as a row.
struct RegionRow: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    let region: ShellRegion
    let items: [RegionItem]
    var spacing: CGFloat = 4

    var body: some View {
        HStack(spacing: spacing) {
            ForEach(items) { item in
                ShellComponentView(item: item, region: region)
            }
        }
        .glassGroup(spacing: spacing, enabled: theme.glassEnabled)
    }
}

/// One region drawn as a column (Opera's rail).
struct RegionColumn: View {
    let region: ShellRegion
    let items: [RegionItem]

    var body: some View {
        VStack(spacing: 6) {
            ForEach(items) { item in
                ShellComponentView(item: item, region: region)
            }
        }
    }
}

struct ShellComponentView: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    let item: RegionItem
    let region: ShellRegion

    var body: some View {
        let layout = theme.layout
        let tab = model.selectedTab
        switch item.component {
        case .back:
            IconButton(symbol: "chevron.left", label: "Back", shortcut: "⌘[", enabled: tab?.canGoBack ?? false) {
                model.goBack()
            }
        case .forward:
            IconButton(symbol: "chevron.right", label: "Forward", shortcut: "⌘]", enabled: tab?.canGoForward ?? false) {
                model.goForward()
            }
        case .reload:
            if tab?.isLoading == true {
                IconButton(symbol: "xmark", label: "Stop", shortcut: "⌘.", enabled: true) { model.stopLoading() }
            } else {
                IconButton(symbol: "arrow.clockwise", label: "Reload", shortcut: "⌘R",
                           enabled: !(tab?.isStartPage ?? true) && tab?.internalPage == nil) {
                    model.reload()
                }
            }
        case .home:
            IconButton(symbol: "house", label: model.shellSettings.homepage.isEmpty ? "Home (New Tab page)" : "Home",
                       shortcut: "⌘⇧H", enabled: true) { model.goHome() }
        case .addressField:
            AddressField(style: region.isSidebar ? .compact : layout.addressBar, embedsBookmarkStar: item.embedsStar)
        case .tabStrip:
            HorizontalTabStrip(style: layout.tabStyle, newTabAttached: item.attachesNewTab)
        case .sidebarToggle:
            IconButton(symbol: layout.tabPlacement == .sidebarTrailing ? "sidebar.right" : "sidebar.left",
                       label: model.isSidebarVisible ? "Hide Sidebar" : "Show Sidebar", shortcut: "⌘⇧L",
                       enabled: layout.sidebar != .shown || !layout.tabPlacement.isVertical) {
                withAnimation(theme.animation()) { model.toggleSidebar() }
            }
        case .newTab:
            NewTabButton()
        case .downloads:
            // Downloads area's button (progress, panel).
            DownloadsToolbarButton()
        case .blocker:
            BlockerButton()
                .buttonStyle(ToolbarButtonStyle())
                .fixedSize()
        case .extensions:
            HStack(spacing: 2) {
                // Platform area's per-extension buttons, then the manager.
                if region != .rail { ShellExtensionToolbarButtonsMount() }
                IconButton(symbol: "puzzlepiece.extension", label: "Extensions", shortcut: "⌘⇧X", enabled: true) {
                    model.openSettings(.extensions)
                }
            }
        case .settings:
            IconButton(symbol: "gearshape", label: "Settings", shortcut: "⌘,", enabled: true) {
                model.toggleSettings()
            }
        case .bookmarkStar:
            BookmarkStarButton()
        case .engineSwitch:
            EngineSwitchButton(showsName: !region.isSidebar && region != .rail && metrics.density != .compact)
        case .searchEngine:
            SearchEngineButton()
        case .workspaces:
            WorkspaceSwitcher(compact: region == .rail)
        case .menu:
            BrowserMenuButton()
        case .flexibleSpace:
            if region == .rail {
                Spacer(minLength: 8)
            } else {
                WindowDragArea().frame(minWidth: 8, maxWidth: .infinity)
            }
        case .bookmarksPage:
            PageButton(page: .bookmarks, shortcut: "⌥⌘B")
        case .historyPage:
            PageButton(page: .history, shortcut: "⌘Y")
        case .readingListPage:
            PageButton(page: .readingList, shortcut: nil)
        case .downloadsPage:
            PageButton(page: .downloads, shortcut: "⌥⌘L")
        }
    }
}

/// A toolbar icon button with a tooltip naming its shortcut.
struct IconButton: View {
    let symbol: String
    let label: String
    let shortcut: String?
    let enabled: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
        }
        .buttonStyle(ToolbarButtonStyle())
        .disabled(!enabled)
        .help(shortcut.map { "\(label) (\($0))" } ?? label)
        .accessibilityLabel(label)
    }
}

private struct PageButton: View {
    @EnvironmentObject private var model: BrowserModel
    let page: InternalPage
    let shortcut: String?

    var body: some View {
        let isOpen = model.selectedTab?.internalPage == page && model.settingsCategory == nil
        Button {
            model.closeSettings()
            model.openInternalPage(page)
        } label: {
            Image(systemName: SiteIcon.symbol(for: page))
                .symbolVariant(isOpen ? .fill : .none)
        }
        .buttonStyle(ToolbarButtonStyle())
        .help(shortcut.map { "\(page.title) (\($0))" } ?? page.title)
        .accessibilityLabel(page.title)
        .accessibilityAddTraits(isOpen ? .isSelected : [])
    }
}

/// The workspace switcher: the current workspace's symbol (and a dot per workspace); the
/// menu switches, creates and deletes workspaces (tabs area's behaviour, via TabsBridge).
struct WorkspaceSwitcher: View {
    @EnvironmentObject private var model: BrowserModel
    var compact = false
    @State private var pendingDeletion: Workspace?

    var body: some View {
        let current = model.workspaces.first { $0.id == model.selectedWorkspaceID } ?? model.workspaces.first
        Menu {
            Section("Workspaces") {
                ForEach(model.workspaces) { workspace in
                    Toggle(isOn: Binding(
                        get: { workspace.id == model.selectedWorkspaceID },
                        set: { if $0 { model.shellSelectWorkspace(workspace.id) } }
                    )) {
                        Label(workspace.name, systemImage: workspace.symbol)
                    }
                }
            }
            if TabsBridge.tabsBehaviourAvailable {
                Divider()
                Button("New Workspace") { model.shellCreateWorkspace() }
                if let current, model.workspaces.count > 1 {
                    Button("Delete “\(current.name)”…") { requestDeletion(current) }
                }
            }
            Divider()
            Button("Manage Workspaces…") { model.showSettings(.tabs) }
        } label: {
            HStack(spacing: 5) {
                Image(systemName: current?.symbol ?? "square.stack")
                if !compact, model.workspaces.count > 1 {
                    // One dot per workspace, like Arc's space switcher.
                    HStack(spacing: 3) {
                        ForEach(model.workspaces) { workspace in
                            Circle()
                                .fill(Color.primary.opacity(workspace.id == model.selectedWorkspaceID ? 0.75 : 0.25))
                                .frame(width: 5, height: 5)
                        }
                    }
                }
            }
            .padding(.horizontal, 6)
        }
        .menuStyle(.button)
        .buttonStyle(ToolbarButtonStyle())
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Workspace: \(current?.name ?? "")")
        .accessibilityLabel("Workspace")
        .accessibilityValue(current?.name ?? "")
        .confirmationDialog(
            "Delete the workspace “\(pendingDeletion?.name ?? "")”?",
            isPresented: Binding(get: { pendingDeletion != nil }, set: { if !$0 { pendingDeletion = nil } }),
            presenting: pendingDeletion
        ) { workspace in
            Button("Delete Workspace and Close Its Tabs", role: .destructive) { model.shellDeleteWorkspace(workspace.id) }
            Button("Cancel", role: .cancel) {}
        } message: { _ in
            Text("Its tabs are closed. You can reopen them with History › Reopen Closed Tab.")
        }
    }

    private func requestDeletion(_ workspace: Workspace) {
        if model.shellWorkspaceDeletionNeedsConfirmation(workspace.id) {
            pendingDeletion = workspace
        } else {
            model.shellDeleteWorkspace(workspace.id)
        }
    }
}

/// Everything in one menu (Chrome's ⋮): new tab, Mosaics, pages, extensions, settings.
struct BrowserMenuButton: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        Menu {
            BrowserMenuContent()
        } label: {
            Image(systemName: "ellipsis")
                .rotationEffect(.degrees(90))
        }
        .menuStyle(.button)
        .buttonStyle(ToolbarButtonStyle())
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Menu")
        .accessibilityLabel("Browser menu")
    }
}

struct BrowserMenuContent: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        let layout = model.mosaics.layout
        Button("New Tab") { model.openNewTabFromShell() }
        Divider()
        Menu("Mosaic") {
            ForEach(MosaicPresetID.allCases) { preset in
                Toggle(preset.title, isOn: Binding(
                    get: { model.mosaics.activePreset == preset },
                    set: { if $0 { model.applyMosaic(preset) } }))
            }
            Divider()
            Button("Customise…") { model.showSettings(.mosaics) }
        }
        if layout.hasSidebar {
            Button(model.isSidebarVisible ? "Hide Sidebar" : "Show Sidebar") { model.toggleSidebar() }
        }
        Toggle("Bookmarks Bar in This Mosaic", isOn: Binding(
            get: { model.mosaics.layout.bookmarksBar },
            set: { value in model.mosaics.update { $0.bookmarksBar = value } }))
        Divider()
        Button("Bookmark This Page") { model.toggleBookmarkForSelectedTab() }
            .disabled(!(model.selectedTab?.isBookmarkable ?? false))
        ForEach([InternalPage.bookmarks, .history, .readingList, .downloads], id: \.self) { page in
            Button(page.title) {
                model.closeSettings()
                model.openInternalPage(page)
            }
        }
        Divider()
        EngineMenuItems()
        Divider()
        Button("Extensions") { model.openSettings(.extensions) }
        Button("Settings…") { model.showSettings() }
    }
}
