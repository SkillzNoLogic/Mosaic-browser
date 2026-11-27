import AppKit
import MosaicCore
import SwiftUI

/// Internal page `mosaic://bookmarks`: the bookmark manager. Owner: **library**.
///
/// Folders and tags on the left; the selected folder's contents (or search results) on the
/// right. Double-click or Return opens, ⌘-double-click opens in a new tab, Delete removes,
/// rows drag to reorder or onto a folder (either side) to move.
struct BookmarksPage: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        BookmarksManager(library: model.library)
            .libraryPageBackground()
    }
}

private enum SidebarItem: Hashable {
    case folder(String)
    case tag(String)
}

private struct FolderNode: Identifiable, Hashable {
    let id: String
    let title: String
    let symbol: String
    var children: [FolderNode]?
}

private struct BookmarksManager: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var library: Library

    @State private var sidebar: SidebarItem? = .folder(Bookmark.barID)
    @State private var query = ""
    @State private var selection: Set<String> = []
    @State private var editing: String?
    @State private var creatingFolderIn: String?
    @State private var pendingDelete: [String] = []
    @FocusState private var searchFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            LibraryPageHeader(title: "Bookmarks", subtitle: subtitle) {
                LibrarySearchField(prompt: "Search bookmarks", text: $query, focus: $searchFocused)
                Button {
                    creatingFolderIn = currentFolderID ?? Bookmark.otherID
                } label: {
                    Label("New Folder", systemImage: "folder.badge.plus")
                }
                .help("New Folder")
                Menu {
                    Button("Import Bookmarks…") { LibraryCommands.importBookmarks(model: model) }
                    if library.chromeBookmarksFile != nil {
                        Button("Import from Google Chrome") { LibraryCommands.importFromChrome(model: model) }
                    }
                    Divider()
                    Button("Export Bookmarks…") { LibraryCommands.exportBookmarks(model: model) }
                } label: {
                    Label("Import and Export", systemImage: "square.and.arrow.up.on.square")
                }
                .menuIndicator(.hidden)
                .fixedSize()
                .help("Import or export bookmarks")
            }
            .labelStyle(.iconOnly)
            .controlSize(.large)
            LibraryStorageWarning(message: library.storageProblem)
            Divider()
            HStack(spacing: 0) {
                sidebarList
                    .frame(width: 240)
                Divider()
                VStack(spacing: 0) {
                    contentHeader
                    contentList
                }
            }
        }
        .sheet(item: Binding(get: { editing.map(Identified.init) }, set: { editing = $0?.id })) { target in
            BookmarkEditor(library: library, bookmarkID: target.id) { editing = nil }
        }
        .sheet(item: Binding(get: { creatingFolderIn.map(Identified.init) }, set: { creatingFolderIn = $0?.id })) { target in
            NewFolderSheet(library: library, parentID: target.id) { folder in
                creatingFolderIn = nil
                if let folder { sidebar = .folder(folder.id) }
            }
        }
        .confirmationDialog(deleteQuestion, isPresented: Binding(
            get: { !pendingDelete.isEmpty }, set: { if !$0 { pendingDelete = [] } })
        ) {
            Button("Delete", role: .destructive) {
                library.deleteBookmarks(pendingDelete)
                selection.subtract(pendingDelete)
                pendingDelete = []
            }
        } message: {
            Text("Folders are deleted with everything inside them.")
        }
        .onChange(of: sidebar) { _, _ in
            selection = []
            if !query.isEmpty { query = "" }
        }
        .onChange(of: library.bookmarks) { _, bookmarks in
            // The selected folder was deleted elsewhere: fall back to the bar.
            if case .folder(let id) = sidebar, !bookmarks.contains(where: { $0.id == id }) {
                sidebar = .folder(Bookmark.barID)
            }
        }
    }

    private struct Identified: Identifiable { let id: String }

    // MARK: Data

    private var subtitle: String {
        let count = library.bookmarks.filter { !$0.isFolder }.count
        return count == 1 ? "1 bookmark" : "\(count) bookmarks"
    }

    private var currentFolderID: String? {
        if case .folder(let id) = sidebar { return id }
        return nil
    }

    private var isSearching: Bool { !query.trimmingCharacters(in: .whitespaces).isEmpty }

    private var items: [Bookmark] {
        if isSearching { return library.searchBookmarks(query) }
        switch sidebar {
        case .folder(let id): return library.children(of: id)
        case .tag(let tag): return library.bookmarks(taggedWith: tag)
        case nil: return []
        }
    }

    private var folderNodes: [FolderNode] {
        func node(_ folder: Bookmark, symbol: String) -> FolderNode {
            let subfolders = library.children(of: folder.id).filter(\.isFolder)
            return FolderNode(id: folder.id, title: folder.displayTitle, symbol: symbol,
                              children: subfolders.isEmpty ? nil : subfolders.map { node($0, symbol: "folder") })
        }
        return [Bookmark.barID, Bookmark.otherID].compactMap { id in
            library.bookmark(id: id).map { node($0, symbol: id == Bookmark.barID ? "menubar.rectangle" : "folder") }
        }
    }

    private func path(of id: String?) -> String {
        var names: [String] = []
        var cursor = id
        while let current = cursor, let folder = library.bookmark(id: current) {
            names.insert(folder.displayTitle, at: 0)
            cursor = folder.parentID
        }
        return names.joined(separator: " › ")
    }

    private var deleteQuestion: String {
        let folders = pendingDelete.compactMap { library.bookmark(id: $0) }.filter(\.isFolder).count
        if pendingDelete.count == 1 {
            return folders == 1 ? "Delete this folder?" : "Delete this bookmark?"
        }
        return "Delete \(pendingDelete.count) items?"
    }

    // MARK: Sidebar

    private var sidebarList: some View {
        List(selection: $sidebar) {
            Section("Folders") {
                OutlineGroup(folderNodes, children: \.children) { node in
                    Label(node.title, systemImage: node.symbol)
                        .tag(SidebarItem.folder(node.id))
                        .dropDestination(for: String.self) { payloads, _ in
                            move(payloads, into: node.id)
                        }
                        .contextMenu { folderMenu(node.id) }
                }
            }
            if !library.allTags.isEmpty {
                Section("Tags") {
                    ForEach(library.allTags, id: \.self) { tag in
                        Label(tag, systemImage: "tag").tag(SidebarItem.tag(tag))
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .accessibilityLabel("Bookmark folders")
    }

    @ViewBuilder
    private func folderMenu(_ id: String) -> some View {
        Button("Open All in New Tabs") { openAll(in: id) }
        Button("New Folder Inside…") { creatingFolderIn = id }
        if !Bookmark.rootIDs.contains(id) {
            Divider()
            Button("Rename…") { editing = id }
            Button("Delete…", role: .destructive) { pendingDelete = [id] }
        }
    }

    // MARK: Content

    private var contentHeader: some View {
        HStack(spacing: 10) {
            Group {
                if isSearching {
                    Text("Results for “\(query)”")
                } else if case .tag(let tag) = sidebar {
                    Label(tag, systemImage: "tag")
                } else {
                    Text(path(of: currentFolderID))
                }
            }
            .font(.headline)
            .lineLimit(1)
            Spacer()
            if !selection.isEmpty {
                Text("\(selection.count) selected").foregroundStyle(.secondary).font(.callout)
                Button("Delete", role: .destructive) { pendingDelete = Array(selection) }
            }
            if !isSearching, let id = currentFolderID, items.contains(where: { !$0.isFolder }) {
                Button("Open All") { openAll(in: id) }
                    .help("Open every bookmark in this folder in new tabs")
            }
        }
        .controlSize(.small)
        .padding(.horizontal, 18)
        .frame(height: 40)
    }

    @ViewBuilder private var contentList: some View {
        let rows = items
        if rows.isEmpty {
            emptyState
        } else {
            List(selection: $selection) {
                ForEach(rows) { item in
                    BookmarkRow(item: item, folderPath: isSearching || sidebar.isTag ? path(of: item.parentID) : nil)
                        .tag(item.id)
                        .draggable(BookmarkDrag.payload(item.id))
                        .dropDestination(for: String.self) { payloads, _ in
                            if item.isFolder { return move(payloads, into: item.id) }
                            guard !isSearching, let parent = item.parentID else { return false }
                            return move(payloads, into: parent, position: item.position)
                        }
                }
            }
            .listStyle(.inset)
            .contextMenu(forSelectionType: String.self) { ids in
                itemMenu(ids)
            } primaryAction: { ids in
                open(ids)
            }
            .onDeleteCommand { if !selection.isEmpty { pendingDelete = Array(selection) } }
            .accessibilityLabel("Bookmarks")
        }
    }

    @ViewBuilder private var emptyState: some View {
        if isSearching {
            ContentUnavailableView.search(text: query)
        } else if currentFolderID == Bookmark.barID {
            ContentUnavailableView {
                Label("The Bookmarks Bar Is Empty", systemImage: "menubar.rectangle")
            } description: {
                Text("Press ⌘D on any page to bookmark it, or drag bookmarks here from another folder.")
            } actions: {
                Button("Import Bookmarks…") { LibraryCommands.importBookmarks(model: model) }
            }
        } else {
            ContentUnavailableView("No Bookmarks", systemImage: "book",
                                   description: Text("Drag bookmarks here to file them in this folder."))
        }
    }

    @ViewBuilder
    private func itemMenu(_ ids: Set<String>) -> some View {
        let chosen = ids.compactMap { library.bookmark(id: $0) }
        if chosen.count == 1, let item = chosen.first {
            if let url = item.url {
                Button("Open") { libraryOpen(url, in: model) }
                Button("Open in New Tab") { libraryOpen(url, in: model, newTab: true) }
                Divider()
                Button("Copy Address") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(url, forType: .string)
                }
            } else {
                Button("Open Folder") { sidebar = .folder(item.id) }
                Button("Open All in New Tabs") { openAll(in: item.id) }
            }
            Button(item.isFolder ? "Rename…" : "Edit…") { editing = item.id }
            moveMenu(chosen)
            Divider()
            Button("Delete", role: .destructive) { pendingDelete = [item.id] }
        } else if !chosen.isEmpty {
            let urls = chosen.compactMap(\.url)
            if !urls.isEmpty {
                Button("Open \(urls.count) in New Tabs") { for url in urls { model.newTab(url: url, select: false) } }
            }
            moveMenu(chosen)
            Divider()
            Button("Delete \(chosen.count) Items", role: .destructive) { pendingDelete = chosen.map(\.id) }
        }
    }

    private func moveMenu(_ items: [Bookmark]) -> some View {
        Menu("Move To") {
            ForEach(library.folderTree(), id: \.folder.id) { entry in
                Button(String(repeating: "   ", count: entry.depth) + entry.folder.displayTitle) {
                    for item in items { library.moveBookmark(id: item.id, to: entry.folder.id) }
                }
                .disabled(items.contains { $0.id == entry.folder.id })
            }
        }
    }

    // MARK: Actions

    private func open(_ ids: Set<String>) {
        let chosen = ids.compactMap { library.bookmark(id: $0) }
        if chosen.count == 1, let item = chosen.first {
            if let url = item.url { libraryOpen(url, in: model) } else { sidebar = .folder(item.id) }
        } else {
            for url in chosen.compactMap(\.url) { model.newTab(url: url, select: false) }
        }
    }

    private func openAll(in folderID: String) {
        for url in library.children(of: folderID).compactMap(\.url) { model.newTab(url: url, select: false) }
    }

    /// Moves dragged bookmarks into `folderID` (at `position`, or the end).
    @discardableResult
    private func move(_ payloads: [String], into folderID: String, position: Int? = nil) -> Bool {
        let ids = payloads.compactMap(BookmarkDrag.id(from:)).filter { $0 != folderID }
        guard !ids.isEmpty else { return false }
        for (offset, id) in ids.enumerated() {
            library.moveBookmark(id: id, to: folderID, position: position.map { $0 + offset })
        }
        return true
    }
}

private extension Optional where Wrapped == SidebarItem {
    var isTag: Bool { if case .tag = self { return true } else { return false } }
}

private struct BookmarkRow: View {
    let item: Bookmark
    /// Shown for search and tag results, where rows come from different folders.
    let folderPath: String?

    var body: some View {
        HStack(spacing: 12) {
            if item.isFolder {
                Image(systemName: "folder.fill")
                    .font(.system(size: 17))
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 26, height: 26)
            } else {
                SiteMonogram(url: item.url ?? "", size: 26)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(item.displayTitle)
                    .font(.system(size: 13, weight: .medium))
                    .lineLimit(1)
                HStack(spacing: 6) {
                    if let url = item.url {
                        Text(LibraryURL.display(url)).lineLimit(1).truncationMode(.middle)
                    } else {
                        Text("Folder")
                    }
                    if let folderPath, !folderPath.isEmpty {
                        Text("·")
                        Label(folderPath, systemImage: "folder").labelStyle(.titleAndIcon).lineLimit(1)
                    }
                }
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            }
            Spacer(minLength: 12)
            if !item.tags.isEmpty {
                HStack(spacing: 4) {
                    ForEach(item.tags.prefix(3), id: \.self) { TagChip(tag: $0) }
                }
            }
            Text(item.created.libraryShortDescription)
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)
                .monospacedDigit()
                .frame(width: 88, alignment: .trailing)
        }
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityLabel(item.isFolder ? "\(item.displayTitle), folder" : "\(item.displayTitle), \(item.url ?? "")")
    }
}
