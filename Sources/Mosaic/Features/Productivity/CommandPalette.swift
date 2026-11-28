import AppKit
import Combine
import MosaicCore

/// One entry in the command palette: something to do, a tab to switch to, a page to open.
///
/// Other areas add their own actions by registering a provider with `CommandPalette`
/// (see `register(_:provider:)`); a provider is asked for its actions every time the
/// palette's results are computed, so titles can reflect the current state ("Mute Tab" /
/// "Unmute Tab") and unavailable actions can simply be left out.
struct PaletteAction: Identifiable {
    enum Kind: String {
        case action, tab, bookmark, history, setting, web

        /// Shown at the right of a row that has no shortcut.
        var label: String? {
            switch self {
            case .action: nil
            case .tab: "Tab"
            case .bookmark: "Bookmark"
            case .history: "History"
            case .setting: "Settings"
            case .web: nil
            }
        }
    }

    /// Stable and unique, namespaced by area ("tabs.pin", "downloads.show"): used to rank
    /// recently chosen actions higher and by automation.
    let id: String
    var title: String
    /// Secondary line (an address, the current value…).
    var subtitle: String?
    /// Other words people might type for it ("preferences" for Settings).
    var keywords: [String] = []
    /// SF Symbol name.
    var symbol: String
    var kind: Kind = .action
    /// Shown as a hint. For a menu command use `ShortcutManager.combo(forSelector:)` so the
    /// user's customised shortcut is shown.
    var shortcut: KeyCombo?
    /// Ranking nudge; leave at 0 unless an action should win ties.
    var boost: Double = 0
    /// Runs after the palette has closed.
    var perform: @MainActor () -> Void
}

/// The command palette (⌘K): an action registry plus the search over actions, open tabs,
/// bookmarks and history (`Library.suggestions`) and settings categories.
@MainActor
final class CommandPalette: ObservableObject {
    typealias Provider = @MainActor (BrowserModel) -> [PaletteAction]

    @Published private(set) var isPresented = false
    @Published var query = "" {
        didSet {
            guard query != oldValue else { return }
            selection = 0
            refresh()
        }
    }
    @Published private(set) var results: [PaletteAction] = []
    /// Index into `results` of the highlighted row.
    @Published var selection = 0

    private unowned let model: BrowserModel
    private var providers: [(id: String, provider: Provider)] = []
    private var recents: PaletteRecents
    private static let recentsKey = "productivity.palette.recents"
    static let resultLimit = 60

    init(model: BrowserModel) {
        self.model = model
        recents = PaletteRecents(ids: AppPaths.defaults.stringArray(forKey: Self.recentsKey) ?? [])
    }

    // MARK: Registry

    /// Adds (or replaces) the actions source named `id`. Call once, e.g. from your service's
    /// set-up; the provider is evaluated each time results are computed.
    func register(_ id: String, provider: @escaping Provider) {
        providers.removeAll { $0.id == id }
        providers.append((id, provider))
        if isPresented { refresh() }
    }

    func unregister(_ id: String) {
        providers.removeAll { $0.id == id }
    }

    /// Every registered action, in registration order (no tabs, library or settings).
    var registeredActions: [PaletteAction] {
        providers.flatMap { $0.provider(model) }
    }

    // MARK: Presenting

    func present() {
        query = ""
        selection = 0
        refresh()
        isPresented = true
        // Keyboard input must come to Mosaic, also when a Gecko page (another app) has focus.
        if model.allowsFocusChanges {
            NSApp.activate(ignoringOtherApps: true)
            (NSApp.mainWindow ?? NSApp.windows.first { $0.canBecomeMain })?.makeKeyAndOrderFront(nil)
        }
    }

    /// `restoringFocus`: hand the keyboard back to the page (when closed without choosing).
    func dismiss(restoringFocus: Bool = true) {
        guard isPresented else { return }
        isPresented = false
        if restoringFocus { model.focusContent() }
    }

    func toggle() {
        isPresented ? dismiss() : present()
    }

    func moveSelection(_ offset: Int) {
        guard !results.isEmpty else { return }
        selection = (selection + offset + results.count) % results.count
    }

    func performSelection() {
        guard results.indices.contains(selection) else { return }
        perform(results[selection])
    }

    func perform(_ action: PaletteAction) {
        if action.kind == .action || action.kind == .setting {
            recents.note(action.id)
            AppPaths.defaults.set(recents.ids, forKey: Self.recentsKey)
        }
        dismiss(restoringFocus: false)
        // After the overlay has gone, so focus requests made by the action stick.
        DispatchQueue.main.async { action.perform() }
    }

    /// Runs the action with `id` among the current candidates for `query` (automation).
    @discardableResult
    func perform(id: String) -> Bool {
        guard let action = candidates(for: query).first(where: { $0.id == id }) ?? results.first(where: { $0.id == id })
        else { return false }
        perform(action)
        return true
    }

    // MARK: Results

    func refresh() {
        results = computeResults(for: query)
        selection = results.isEmpty ? 0 : min(selection, results.count - 1)
        if selection < 0 { selection = 0 }
    }

    func computeResults(for query: String) -> [PaletteAction] {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let all = candidates(for: trimmed)
        let byID = Dictionary(all.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let ranked = PaletteRanker.rank(
            all.map { PaletteCandidate(id: $0.id, title: $0.title, keywords: $0.keywords + [$0.subtitle ?? ""], boost: $0.boost) },
            query: trimmed, recent: recents.ids, limit: Self.resultLimit)
        var results = ranked.compactMap { byID[$0.id] }
        if !trimmed.isEmpty { results += webFallbacks(for: trimmed) }
        return results
    }

    /// Everything the palette can offer for `query`: registered actions, open tabs,
    /// settings categories and (when something is typed) the library's suggestions.
    private func candidates(for query: String) -> [PaletteAction] {
        var items = registeredActions
        // Other open tabs (switching to the current one would do nothing).
        items += model.tabs.filter { $0.id != model.selectedTabID }.map { tab in
            PaletteAction(
                id: "tab:\(tab.id.uuidString)", title: tab.displayTitle,
                subtitle: tab.isStartPage ? nil : tab.url,
                keywords: ["tab", tab.engine.displayName] + (URL(string: tab.url)?.host.map { [$0] } ?? []),
                symbol: "square.on.square", kind: .tab, boost: 6
            ) { [weak model, weak tab] in
                if let model, let tab { model.select(tab) }
            }
        }
        items += SettingsCategory.allCases.map { category in
            PaletteAction(
                id: "settings.\(category.rawValue)", title: "\(category.title) Settings",
                keywords: ["settings", "preferences", "options"], symbol: category.symbol, kind: .setting, boost: 2
            ) { [weak model] in model?.openSettings(category) }
        }
        if !query.isEmpty {
            let openURLs = Set(model.tabs.map(\.url))
            for suggestion in model.library.suggestions(for: query) {
                guard let action = libraryAction(suggestion, openURLs: openURLs) else { continue }
                items.append(action)
            }
        }
        return items
    }

    private func libraryAction(_ suggestion: Library.Suggestion, openURLs: Set<String>) -> PaletteAction? {
        let kind: PaletteAction.Kind
        let symbol: String
        switch suggestion.kind {
        case .bookmark: (kind, symbol) = (.bookmark, "star")
        case .history: (kind, symbol) = (.history, "clock")
        // Open tabs are listed from the model; searches are the web fallback below.
        case .openTab, .search: return nil
        }
        // A page already open in a tab is offered as that tab.
        if openURLs.contains(suggestion.value) { return nil }
        let value = suggestion.value
        return PaletteAction(
            id: "\(kind.rawValue):\(value)", title: suggestion.title.isEmpty ? value : suggestion.title,
            subtitle: value, symbol: symbol, kind: kind, boost: kind == .bookmark ? 3 : 0
        ) { [weak self] in self?.open(value) }
    }

    /// Typing an address offers to open it; anything typed offers a web search.
    private func webFallbacks(for query: String) -> [PaletteAction] {
        var items: [PaletteAction] = []
        if AddressResolver.isAddress(query), let url = AddressResolver.resolve(query, searchEngine: model.searchEngine) {
            items.append(PaletteAction(id: "web.open", title: "Open \(query)", subtitle: url.absoluteString,
                                       symbol: "globe", kind: .web) { [weak self] in self?.open(url.absoluteString) })
        }
        let engine = model.searchEngine
        if let url = engine.searchURL(for: query) {
            items.append(PaletteAction(id: "web.search", title: "Search \(engine.name) for “\(query)”",
                                       symbol: "magnifyingglass", kind: .web) { [weak self] in self?.open(url.absoluteString) })
        }
        return items
    }

    /// Opens an address from the palette: in the current tab when it is a new-tab page,
    /// otherwise in a new tab next to it.
    private func open(_ url: String) {
        if let tab = model.selectedTab, tab.isStartPage {
            model.load(url, in: tab)
            model.focusContent()
        } else {
            model.newTab(url: url)
        }
    }
}
