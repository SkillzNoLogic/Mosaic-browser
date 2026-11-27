import MosaicCore
import SwiftUI

/// Tab search (⌘⇧A): every open tab of every workspace, filtered as you type, plus
/// recently closed tabs. ↑/↓ choose, Return switches (to another workspace if needed),
/// Escape closes. Self-contained: give it the model; `TabsOverlay` shows it when
/// `tabsFeature.isTabSearchVisible`, and the command palette can host it too.
struct TabSearchPanel: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject private var feature: TabsFeature
    @State private var query = ""
    @State private var highlighted = 0
    @FocusState private var fieldFocused: Bool

    init(model: BrowserModel) {
        self.model = model
        _feature = ObservedObject(wrappedValue: model.tabsFeature)
    }

    private enum Row: Identifiable {
        case open(BrowserTab)
        case closed(ClosedItem)

        var id: String {
            switch self {
            case .open(let tab): "open-\(tab.id)"
            case .closed(let item): "closed-\(item.id)"
            }
        }
    }

    private var openRows: [Row] { model.tabSearchResults(query).map(Row.open) }

    private var closedRows: [Row] {
        let items = feature.recentlyClosed.items.filter { item in
            TabSearch.score(query: query, title: item.title, url: item.tabs.first?.url ?? "") != nil
        }
        return items.prefix(query.isEmpty ? 5 : 10).map(Row.closed)
    }

    var body: some View {
        let open = openRows
        let closed = closedRows
        let rows = open + closed
        VStack(spacing: 0) {
            searchField(rows: rows)
            Divider()
            if rows.isEmpty {
                Text("No tabs match “\(query)”")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 28)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            if !open.isEmpty { header("Open Tabs · \(open.count)") }
                            ForEach(Array(open.enumerated()), id: \.element.id) { index, row in
                                rowView(row, index: index).id(row.id)
                            }
                            if !closed.isEmpty { header("Recently Closed") }
                            ForEach(Array(closed.enumerated()), id: \.element.id) { offset, row in
                                rowView(row, index: open.count + offset).id(row.id)
                            }
                        }
                        .padding(6)
                    }
                    .onChange(of: highlighted) { _, index in
                        if rows.indices.contains(index) { proxy.scrollTo(rows[index].id) }
                    }
                }
            }
        }
        .frame(width: 560)
        .frame(maxHeight: 460)
        .fixedSize(horizontal: false, vertical: true)
        .tabsCard()
        .onAppear {
            query = ""
            highlighted = 0
            fieldFocused = true
        }
        .onChange(of: query) { _, _ in highlighted = 0 }
        .onExitCommand { model.hideTabSearch() }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Tab search")
    }

    private func searchField(rows: [Row]) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField("Search open tabs", text: $query)
                .textFieldStyle(.plain)
                .font(.system(size: 15))
                .focused($fieldFocused)
                .onSubmit { activate(rows: rows) }
                .onKeyPress(.downArrow) {
                    highlighted = min(highlighted + 1, max(rows.count - 1, 0))
                    return .handled
                }
                .onKeyPress(.upArrow) {
                    highlighted = max(highlighted - 1, 0)
                    return .handled
                }
                .onKeyPress(.escape) {
                    model.hideTabSearch()
                    return .handled
                }
                .accessibilityLabel("Search open tabs")
            Text("⌘⇧A").font(.caption).foregroundStyle(.tertiary)
        }
        .padding(.horizontal, 14)
        .frame(height: 44)
    }

    private func header(_ title: String) -> some View {
        Text(title)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 8)
            .padding(.top, 6)
            .padding(.bottom, 2)
            .accessibilityAddTraits(.isHeader)
    }

    private func activate(rows: [Row]) {
        guard rows.indices.contains(highlighted) else { return }
        activate(rows[highlighted])
    }

    private func activate(_ row: Row) {
        model.hideTabSearch()
        switch row {
        case .open(let tab): model.select(tab)
        case .closed(let item): model.reopenClosed(item.id)
        }
    }

    @ViewBuilder
    private func rowView(_ row: Row, index: Int) -> some View {
        let isHighlighted = index == highlighted
        HStack(spacing: 10) {
            switch row {
            case .open(let tab): OpenTabRow(model: model, tab: tab)
            case .closed(let item): ClosedItemRow(item: item)
            }
        }
        .padding(.horizontal, 8)
        .frame(minHeight: 38)
        .background(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(isHighlighted ? Color.accentColor.opacity(0.18) : .clear)
        )
        .contentShape(Rectangle())
        .onTapGesture { activate(row) }
        .onHover { if $0 { highlighted = index } }
        .accessibilityAddTraits(isHighlighted ? [.isButton, .isSelected] : .isButton)
        .accessibilityAction { activate(row) }
    }
}

private struct OpenTabRow: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject var tab: BrowserTab
    @State private var hovering = false

    var body: some View {
        let group = model.group(of: tab)
        let workspace = model.workspace(of: tab)
        let isCurrent = tab.id == model.selectedTabID
        Circle()
            .fill(TabsStyle.engineTint(tab.engine))
            .frame(width: 8, height: 8)
            .opacity(tab.isAsleep ? BrowserTab.asleepOpacity : 1)
            .accessibilityHidden(true)
        VStack(alignment: .leading, spacing: 1) {
            Text(tab.displayTitle)
                .font(.system(size: 13, weight: isCurrent ? .semibold : .regular))
                .lineLimit(1)
            HStack(spacing: 4) {
                if let group {
                    Circle().fill(group.color.swatch).frame(width: 6, height: 6)
                    Text(group.displayName)
                }
                if let workspace, workspace.id != model.selectedWorkspaceID {
                    Image(systemName: workspace.symbol)
                    Text(workspace.name)
                }
                if !tab.isStartPage { Text(URL(string: tab.url)?.host ?? tab.url).lineLimit(1) }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
        .opacity(tab.isAsleep ? BrowserTab.asleepOpacity + 0.2 : 1)
        Spacer(minLength: 6)
        HStack(spacing: 6) {
            if tab.isPinned { Image(systemName: "pin.fill").help("Pinned") }
            if tab.isMuted { Image(systemName: "speaker.slash.fill").help("Muted") }
            if tab.isPlayingAudio, !tab.isMuted { Image(systemName: "speaker.wave.2.fill").help("Playing audio") }
            if tab.isAsleep { Image(systemName: "moon.zzz").help("Unloaded to save memory; loads when selected") }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        Button {
            model.close(tab)
        } label: {
            Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).frame(width: 22, height: 22)
        }
        .buttonStyle(.plain)
        .opacity(hovering ? 0.8 : 0)
        .help("Close Tab")
        .accessibilityLabel("Close \(tab.displayTitle)")
        .onHover { hovering = $0 }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityText(group: group, workspace: workspace))
    }

    private func accessibilityText(group: TabGroup?, workspace: Workspace?) -> String {
        var parts = [tab.displayTitle, tab.engine.displayName]
        if let workspace, workspace.id != model.selectedWorkspaceID { parts.append("in \(workspace.name)") }
        if let group { parts.append("group \(group.displayName)") }
        if tab.isPinned { parts.append("pinned") }
        if tab.isMuted { parts.append("muted") }
        if tab.isAsleep { parts.append("unloaded") }
        return parts.joined(separator: ", ")
    }
}

private struct ClosedItemRow: View {
    let item: ClosedItem

    var body: some View {
        Image(systemName: item.kind == .tab ? "arrow.uturn.backward" : item.kind == .group ? "folder" : "square.stack")
            .foregroundStyle(.secondary)
            .frame(width: 8)
            .accessibilityHidden(true)
        VStack(alignment: .leading, spacing: 1) {
            Text(item.title).font(.system(size: 13)).lineLimit(1)
            Text(item.closedAt, style: .relative)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        Spacer(minLength: 6)
        Text("Reopen").font(.caption).foregroundStyle(.secondary)
    }
}
