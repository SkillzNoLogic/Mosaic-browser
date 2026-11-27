import AppKit
import MosaicCore
import Observation
import SwiftUI

/// Suggestions shared between the address field and the list the shell draws over the page.
@MainActor
@Observable
final class AddressBarState {
    var suggestions: [Library.Suggestion] = []
    /// Highlighted row (arrow keys), or nil.
    var highlighted: Int?
    var isEditing = false

    var showsSuggestions: Bool { isEditing && !suggestions.isEmpty }

    /// Shows the newest rows for what is typed (the list updates as better matches arrive).
    func show(_ rows: [Library.Suggestion]) {
        suggestions = Array(rows.prefix(8))
        if let highlighted, !suggestions.indices.contains(highlighted) { self.highlighted = nil }
    }

    /// Whether `text` is worth suggesting for (not empty, not just the current page).
    static func wantsSuggestions(for text: String, currentURL: String?) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        return !trimmed.isEmpty && trimmed != currentURL
    }

    func moveHighlight(_ delta: Int) {
        guard !suggestions.isEmpty else { return }
        if let current = highlighted {
            let next = current + delta
            highlighted = suggestions.indices.contains(next) ? next : (next < 0 ? nil : current)
        } else if delta > 0 {
            highlighted = 0
        }
    }

    func clear() {
        suggestions = []
        highlighted = nil
    }
}

/// Where the address field is, so the suggestion list can be drawn under it from the root.
struct AddressFieldAnchorKey: PreferenceKey {
    static let defaultValue: Anchor<CGRect>? = nil
    static func reduce(value: inout Anchor<CGRect>?, nextValue: () -> Anchor<CGRect>?) {
        value = value ?? nextValue()
    }
}

/// The address and search field in its three styles.
struct AddressField: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(AddressBarState.self) private var state
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics
    let style: AddressBarStyle
    /// Draw the bookmark star inside the field (the layout has it right after the field).
    var embedsBookmarkStar = false
    @FocusState private var focused: Bool

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: fieldRadius, style: .continuous)
        HStack(spacing: 6) {
            LeadingMenu()
            ZStack(alignment: style == .unified ? .center : .leading) {
                TextField(placeholder, text: $model.addressText)
                    .textFieldStyle(.plain)
                    .font(.system(size: metrics.font))
                    .focused($focused)
                    .opacity(showsSummary ? 0 : 1)
                    .onSubmit(submit)
                    .onKeyPress(.downArrow) {
                        state.moveHighlight(1)
                        return state.suggestions.isEmpty ? .ignored : .handled
                    }
                    .onKeyPress(.upArrow) {
                        state.moveHighlight(-1)
                        return state.suggestions.isEmpty ? .ignored : .handled
                    }
                    .onKeyPress(.escape) {
                        if state.showsSuggestions {
                            state.clear()
                        } else {
                            focused = false
                            model.focusContent()
                        }
                        return .handled
                    }
                    .accessibilityLabel("Address and search")
                    .accessibilityValue(model.addressText)
                if showsSummary {
                    summary.allowsHitTesting(false)
                }
            }
            .frame(maxWidth: .infinity)
            if embedsBookmarkStar { BookmarkStarButton(compact: true) }
            if style == .unified, let tab = model.selectedTab, !tab.isStartPage {
                Button {
                    tab.isLoading ? model.stopLoading() : model.reload()
                } label: {
                    Image(systemName: tab.isLoading ? "xmark" : "arrow.clockwise")
                        .font(.system(size: metrics.icon - 3, weight: .semibold))
                }
                .buttonStyle(ToolbarButtonStyle(width: 24, height: 24))
                .help(tab.isLoading ? "Stop (⌘.)" : "Reload (⌘R)")
                .accessibilityLabel(tab.isLoading ? "Stop" : "Reload")
            }
        }
        .padding(.leading, 6)
        .padding(.trailing, 4)
        .frame(height: metrics.fieldHeight)
        .frame(maxWidth: style == .unified ? 640 : .infinity)
        .chromeSurface(shape, role: .control)
        .overlay {
            shape.strokeBorder(focused ? theme.accent : Color.primary.opacity(theme.glassEnabled ? 0 : 0.06),
                               lineWidth: focused ? (theme.prominentFocusRings ? 3 : 2) : 1)
        }
        .overlay(alignment: .bottom) { progressLine }
        .contentShape(shape)
        .onTapGesture { focused = true }
        .anchorPreference(key: AddressFieldAnchorKey.self, value: .bounds) { $0 }
        .onChange(of: model.addressFocusRequest) { _, _ in focused = true }
        .onChange(of: focused) { _, isFocused in
            model.isEditingAddress = isFocused
            state.isEditing = isFocused
            if !isFocused {
                state.clear()
                if let tab = model.selectedTab { model.addressText = tab.url }
            }
        }
        .onChange(of: model.addressText) { _, text in
            guard focused else { return }
            // A navigation that did not come from this field (menu, start page, Gecko)
            // ends editing in the model; follow it.
            if !model.isEditingAddress {
                focused = false
                return
            }
            state.highlighted = nil
        }
        // Library area's suggestions, refreshed as they come in; restarted on every keystroke.
        .task(id: SuggestionQuery(text: model.addressText, focused: focused)) {
            guard focused, AddressBarState.wantsSuggestions(for: model.addressText, currentURL: model.selectedTab?.url) else {
                state.show([])
                return
            }
            for await rows in model.shellSuggestionStream(for: model.addressText) {
                guard !Task.isCancelled else { break }
                state.show(rows)
            }
        }
        .onAppear {
            // A layout switch can recreate the field while a request is outstanding.
            if model.isEditingAddress { focused = true }
        }
    }

    private var placeholder: String {
        style == .compact ? "Search or enter address" : "Search \(model.searchEngine.name) or enter address"
    }

    private var fieldRadius: CGFloat {
        style == .unified ? max(theme.cornerRadius, 9) : theme.cornerRadius
    }

    /// Safari and Arc show just the site while the field is not being edited.
    private var showsSummary: Bool {
        !focused && style != .omnibox && !(model.selectedTab?.isStartPage ?? true)
    }

    private var summary: some View {
        Text(summaryText)
            .font(.system(size: metrics.font, weight: style == .unified ? .medium : .regular))
            .foregroundStyle(.primary.opacity(0.85))
            .lineLimit(1)
            .truncationMode(.middle)
    }

    private var summaryText: String {
        guard let tab = model.selectedTab else { return "" }
        if let page = tab.internalPage { return page.title }
        guard let url = URL(string: tab.url), let host = url.host else { return tab.url }
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }

    @ViewBuilder private var progressLine: some View {
        if let tab = model.selectedTab, tab.isLoading {
            LoadingBar(color: theme.accent)
                .frame(height: 2)
                .padding(.horizontal, fieldRadius / 2)
                .allowsHitTesting(false)
        }
    }

    private func submit() {
        if let index = state.highlighted, state.suggestions.indices.contains(index) {
            open(state.suggestions[index])
        } else {
            let input = model.addressText
            focused = false
            model.navigate(input)
        }
        state.clear()
    }

    private func open(_ suggestion: Library.Suggestion) {
        focused = false
        model.shellOpen(suggestion)
    }
}

private struct SuggestionQuery: Equatable {
    let text: String
    let focused: Bool
}

/// An indeterminate progress line under the address field.
private struct LoadingBar: View {
    @Environment(\.shellTheme) private var theme
    let color: Color
    @State private var phase: CGFloat = 0

    var body: some View {
        GeometryReader { proxy in
            Capsule()
                .fill(color)
                .frame(width: proxy.size.width * 0.3)
                .offset(x: theme.reduceMotion ? proxy.size.width * 0.35 : phase * proxy.size.width * 0.7)
        }
        .onAppear {
            guard !theme.reduceMotion else { return }
            withAnimation(.easeInOut(duration: 0.9).repeatForever(autoreverses: true)) { phase = 1 }
        }
        .accessibilityHidden(true)
    }
}

/// The field's leading icon: shows whether the page is secure and opens a menu to choose
/// the search engine and the tab's engine.
private struct LeadingMenu: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellMetrics) private var metrics

    var body: some View {
        Menu {
            Picker("Search With", selection: $model.searchEngine) {
                ForEach(SearchEngine.builtIn) { engine in
                    Text(engine.name).tag(engine)
                }
            }
            .pickerStyle(.inline)
            Divider()
            EngineMenuItems()
        } label: {
            Image(systemName: symbol)
                .font(.system(size: metrics.icon - 3, weight: .medium))
                .foregroundStyle(.secondary)
                .frame(width: 22, height: 22)
        }
        .menuStyle(.button)
        .buttonStyle(ToolbarButtonStyle(width: 24, height: 24))
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Search engine and page engine (⌘⇧S cycles search engines)")
        .accessibilityLabel(accessibility)
    }

    private var symbol: String {
        guard let tab = model.selectedTab, !tab.isStartPage, !model.isEditingAddress else { return "magnifyingglass" }
        if tab.internalPage != nil { return "square.grid.2x2" }
        return tab.url.hasPrefix("https://") ? "lock.fill" : "globe"
    }

    private var accessibility: String {
        symbol == "lock.fill" ? "Secure connection; search and engine options" : "Search and engine options"
    }
}

/// "Rendered by" choices for the selected tab, shared by several menus.
struct EngineMenuItems: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        let current = model.selectedTab?.engine ?? model.engine
        Section("This Tab’s Engine") {
            ForEach(EngineKind.allCases) { kind in
                Toggle(isOn: Binding(get: { current == kind }, set: { if $0 { model.setEngine(kind) } })) {
                    Text("\(kind.displayName) (\(kind.engineName))")
                }
                .disabled(kind == .chromium && !model.chromiumAvailable)
            }
        }
    }
}

// MARK: - Suggestions list

/// Drawn by the shell's root over everything, under the address field. Opaque, because it
/// often lies over a Gecko page, which is a separate window.
struct AddressSuggestionsList: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(AddressBarState.self) private var state
    @Environment(\.shellTheme) private var theme
    @Environment(\.shellMetrics) private var metrics

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: max(8, theme.cornerRadius + 2), style: .continuous)
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(state.suggestions.enumerated()), id: \.element.id) { index, suggestion in
                Button {
                    model.shellOpen(suggestion)
                    state.clear()
                } label: {
                    HStack(spacing: 10) {
                        Image(systemName: symbol(for: suggestion.kind))
                            .frame(width: 18)
                            .foregroundStyle(.secondary)
                        Text(suggestion.title.isEmpty ? suggestion.value : suggestion.title)
                            .lineLimit(1)
                        if !suggestion.title.isEmpty, suggestion.kind != .search {
                            Text(suggestion.value)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                        Spacer(minLength: 0)
                        if suggestion.kind == .openTab {
                            Text("Switch to Tab").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .font(.system(size: metrics.font))
                    .padding(.horizontal, 12)
                    .frame(height: metrics.sidebarRowHeight)
                    .background(index == state.highlighted ? theme.accent.opacity(0.18) : .clear)
                    .contentShape(Rectangle())
                }
                .buttonStyle(HoverRowStyle(cornerRadius: 0))
                .accessibilityLabel(suggestion.title.isEmpty ? suggestion.value : "\(suggestion.title), \(suggestion.value)")
            }
        }
        .padding(.vertical, 4)
        .background(shape.fill(ShellColors.overlayPanel))
        .clipShape(shape)
        .overlay(shape.strokeBorder(Color.primary.opacity(0.1)))
        .shadow(color: .black.opacity(0.18), radius: 14, y: 6)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Suggestions")
    }

    private func symbol(for kind: Library.Suggestion.Kind) -> String {
        switch kind {
        case .history: "clock"
        case .bookmark: "star"
        case .search: "magnifyingglass"
        case .openTab: "square.on.square"
        }
    }
}

// MARK: - Small components

struct BookmarkStarButton: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    var compact = false
    /// The bookmark being edited in the popover, with its heading.
    @State private var editing: (id: String, heading: String)?

    var body: some View {
        let bookmarked = model.selectedTabIsBookmarked
        let enabled = model.selectedTab?.isBookmarkable ?? false
        Button {
            starClicked(wasBookmarked: bookmarked)
        } label: {
            Image(systemName: bookmarked ? "star.fill" : "star")
                .foregroundStyle(bookmarked ? theme.accent : Color.primary.opacity(enabled ? 0.75 : 0.3))
        }
        .buttonStyle(ToolbarButtonStyle(width: compact ? 26 : nil, height: compact ? 26 : nil))
        .disabled(!enabled)
        .help(FeatureMounts.libraryAvailable
              ? (bookmarked ? "Edit Bookmark (⌘D)" : "Bookmark This Page (⌘D)")
              : (bookmarked ? "Remove Bookmark" : "Bookmark This Page"))
        .accessibilityLabel(bookmarked ? (FeatureMounts.libraryAvailable ? "Edit bookmark" : "Remove bookmark") : "Bookmark this page")
        .popover(isPresented: Binding(get: { editing != nil }, set: { if !$0 { editing = nil } }), arrowEdge: .bottom) {
            if let editing {
                ShellBookmarkEditorMount(bookmarkID: editing.id, heading: editing.heading) { self.editing = nil }
                    .environmentObject(model)
            }
        }
    }

    /// With the library's editor: add the bookmark if needed and show the editor (Safari and
    /// Chrome behave this way). Without it: a plain on/off toggle.
    private func starClicked(wasBookmarked: Bool) {
        guard FeatureMounts.libraryAvailable, let tab = model.selectedTab else {
            model.toggleBookmarkForSelectedTab()
            return
        }
        if !wasBookmarked { model.toggleBookmarkForSelectedTab() }
        if let id = model.shellBookmarkID(for: tab.url) {
            editing = (id, wasBookmarked ? "Edit Bookmark" : "Bookmark Added")
        }
    }
}

/// Shows and switches the selected tab's engine.
struct EngineSwitchButton: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellMetrics) private var metrics
    var showsName = true

    var body: some View {
        let engine = model.selectedTab?.engine ?? model.engine
        Menu {
            EngineMenuItems()
            Divider()
            Text("Switching reloads the page in the other engine.")
        } label: {
            HStack(spacing: 5) {
                Circle().fill(ShellColors.tint(for: engine)).frame(width: 9, height: 9)
                if showsName {
                    Text(engine.displayName).font(.system(size: metrics.smallFont, weight: .medium))
                }
            }
            .padding(.horizontal, showsName ? 8 : 0)
        }
        .menuStyle(.button)
        .buttonStyle(ToolbarButtonStyle())
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Rendering engine for this tab: \(engine.displayName) (⌘⇧E switches)")
        .accessibilityLabel("Rendering engine")
        .accessibilityValue(engine.displayName)
    }
}

struct SearchEngineButton: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        Menu {
            Picker("Search Engine", selection: $model.searchEngine) {
                ForEach(SearchEngine.builtIn) { engine in Text(engine.name).tag(engine) }
            }
            .pickerStyle(.inline)
        } label: {
            Image(systemName: "text.magnifyingglass")
        }
        .menuStyle(.button)
        .buttonStyle(ToolbarButtonStyle())
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Search engine: \(model.searchEngine.name) (⌘⇧S cycles)")
        .accessibilityLabel("Search engine")
        .accessibilityValue(model.searchEngine.name)
    }
}
