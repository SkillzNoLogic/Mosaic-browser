import MosaicCore
import SwiftUI

/// Settings › Search. Owner: **library**.
///
/// The default search engine, network search suggestions (off by default), and search
/// shortcuts: keywords typed before the terms in the address bar ("w swift" searches
/// Wikipedia), for the built-in engines and for engines the user adds.
struct SearchSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        SearchSettingsForm(library: model.library)
    }
}

private struct SearchSettingsForm: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var library: Library
    @State private var editing: CustomSearchEngine?

    var body: some View {
        Form {
            Section {
                Picker("Default search engine", selection: $model.searchEngine) {
                    ForEach(SearchEngine.builtIn) { Text($0.name).tag($0) }
                    if !library.customEngines.isEmpty {
                        Divider()
                        ForEach(library.customEngines) { Text($0.name).tag($0.searchEngine) }
                    }
                }
                Text("Used when what you type in the address bar is not an address. ⌘⇧S cycles through the built-in engines.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            Section("Address Bar Suggestions") {
                Toggle(isOn: $library.remoteSuggestionsEnabled) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Include search suggestions from \(suggestionProvider)")
                        Text(remoteExplanation)
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                .disabled(SearchShortcuts.suggestTemplate(for: model.searchEngine, custom: library.customEngines) == nil
                          && !library.remoteSuggestionsEnabled)
                LabeledContent("Always on") {
                    Text("History, bookmarks and open tabs, matched on this Mac")
                        .foregroundStyle(.secondary)
                }
            }

            Section {
                ForEach(library.customEngines) { engine in
                    EngineRow(name: engine.name, keyword: engine.keyword, address: engine.template,
                              isDefault: model.searchEngine.id == engine.id) {
                        Menu {
                            Button("Edit…") { editing = engine }
                            Button("Make Default") { model.searchEngine = engine.searchEngine }
                                .disabled(model.searchEngine.id == engine.id)
                            Divider()
                            Button("Delete", role: .destructive) { library.deleteEngine(engine) }
                        } label: {
                            Image(systemName: "ellipsis.circle")
                        }
                        .menuStyle(.borderlessButton)
                        .menuIndicator(.hidden)
                        .fixedSize()
                        .accessibilityLabel("Actions for \(engine.name)")
                    }
                }
                ForEach(SearchEngine.builtIn) { engine in
                    EngineRow(name: engine.name, keyword: SearchShortcuts.keyword(for: engine) ?? "",
                              address: engine.searchTemplate, isDefault: model.searchEngine.id == engine.id) {
                        EmptyView()
                    }
                }
            } header: {
                HStack {
                    Text("Search Shortcuts")
                    Spacer()
                    Button {
                        editing = CustomSearchEngine(name: "", keyword: "", template: "https://")
                    } label: {
                        Label("Add Search Engine…", systemImage: "plus")
                    }
                    .controlSize(.small)
                }
            } footer: {
                Text("Type a keyword, a space and your search in the address bar: “w mosaic browser” searches Wikipedia, “@ddg privacy” searches DuckDuckGo.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .sheet(item: $editing) { engine in
            EngineEditor(library: library, original: engine) { editing = nil }
        }
    }

    private var suggestionProvider: String {
        model.searchEngine.name
    }

    private var remoteExplanation: String {
        if SearchShortcuts.suggestTemplate(for: model.searchEngine, custom: library.customEngines) == nil {
            return "\(model.searchEngine.name) has no suggestion service."
        }
        return "Off by default. When on, what you type in the address bar is sent to \(model.searchEngine.name) as you type (never in private tabs, never for addresses)."
    }
}

private struct EngineRow<Actions: View>: View {
    let name: String
    let keyword: String
    let address: String
    let isDefault: Bool
    @ViewBuilder var actions: Actions

    var body: some View {
        HStack(spacing: 12) {
            SiteMonogram(url: address, size: 22)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(name)
                    if isDefault {
                        Text("Default")
                            .font(.system(size: 10, weight: .semibold))
                            .padding(.horizontal, 6)
                            .padding(.vertical, 1)
                            .background(Capsule().fill(Color.accentColor.opacity(0.15)))
                            .foregroundStyle(Color.accentColor)
                    }
                }
                Text(address)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer()
            if !keyword.isEmpty {
                Text(keyword)
                    .font(.system(size: 12, design: .monospaced))
                    .padding(.horizontal, 7)
                    .padding(.vertical, 2)
                    .background(RoundedRectangle(cornerRadius: 5).fill(Color.primary.opacity(0.07)))
                    .accessibilityLabel("Keyword \(keyword)")
            }
            actions
        }
        .accessibilityElement(children: .contain)
    }
}

private struct EngineEditor: View {
    @ObservedObject var library: Library
    let original: CustomSearchEngine
    var onDone: () -> Void

    @State private var name = ""
    @State private var keyword = ""
    @State private var template = ""
    @State private var suggest = ""
    @State private var loaded = false

    private var isNew: Bool { !library.customEngines.contains { $0.id == original.id } }

    private var problem: String? {
        if let problem = CustomSearchEngine.problem(name: name, keyword: keyword, template: template) { return problem }
        if !library.isKeywordAvailable(keyword, excluding: original.id) { return "“\(keyword)” is already used by another search engine." }
        if !suggest.isEmpty, !suggest.contains("%s") { return "The suggestions address must contain %s." }
        return nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(isNew ? "Add Search Engine" : "Edit Search Engine").font(.headline)
            Form {
                TextField("Name", text: $name, prompt: Text("Wikipedia"))
                TextField("Keyword", text: $keyword, prompt: Text("w"))
                TextField("Search address", text: $template, prompt: Text("https://example.com/search?q=%s"))
                TextField("Suggestions (optional)", text: $suggest, prompt: Text("OpenSearch JSON address with %s"))
            }
            .formStyle(.columns)
            Text("Use %s where the search terms go. Suggestions are only fetched when search suggestions are on.")
                .font(.callout)
                .foregroundStyle(.secondary)
            if let problem, !name.isEmpty || !template.isEmpty {
                Text(problem).font(.callout).foregroundStyle(.red)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: onDone).keyboardShortcut(.cancelAction)
                Button(isNew ? "Add" : "Save") {
                    var engine = original
                    engine.name = name.trimmingCharacters(in: .whitespaces)
                    engine.keyword = keyword.trimmingCharacters(in: .whitespaces)
                    engine.template = template.trimmingCharacters(in: .whitespaces)
                    let suggestion = suggest.trimmingCharacters(in: .whitespaces)
                    engine.suggestTemplate = suggestion.isEmpty ? nil : suggestion
                    library.saveEngine(engine)
                    onDone()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(problem != nil)
            }
        }
        .padding(20)
        .frame(width: 480)
        .onAppear {
            guard !loaded else { return }
            loaded = true
            name = original.name
            keyword = original.keyword
            template = original.template
            suggest = original.suggestTemplate ?? ""
        }
    }
}
