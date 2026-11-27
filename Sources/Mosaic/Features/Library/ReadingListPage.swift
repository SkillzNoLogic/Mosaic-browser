import AppKit
import MosaicCore
import SwiftUI

/// Internal page `mosaic://reading-list`. Owner: **library**.
///
/// Pages saved to read later (Bookmarks › Add to Reading List, ⇧⌘D). Opening one marks it
/// read; read pages stay until removed.
struct ReadingListPage: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        ReadingListBrowser(library: model.library)
            .libraryPageBackground()
    }
}

private struct ReadingListBrowser: View {
    enum Filter: String, CaseIterable, Identifiable {
        case unread = "Unread", all = "All"
        var id: String { rawValue }
    }

    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var library: Library
    @State private var filter = Filter.unread
    @State private var query = ""
    @State private var selection: Set<String> = []

    var body: some View {
        VStack(spacing: 0) {
            LibraryPageHeader(title: "Reading List", subtitle: subtitle) {
                LibrarySearchField(prompt: "Search Reading List", text: $query)
                Picker("Show", selection: $filter) {
                    ForEach(Filter.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
            }
            .controlSize(.large)
            LibraryStorageWarning(message: library.storageProblem)
            Divider()
            content
        }
    }

    private var subtitle: String {
        let unread = library.readingList.filter { !$0.isRead }.count
        return "\(unread) unread · \(library.readingList.count) saved"
    }

    private var items: [ReadingListItem] {
        let terms = query.lowercased().split(whereSeparator: \.isWhitespace)
        return library.readingList.filter { item in
            (filter == .all || !item.isRead)
                && terms.allSatisfy { item.title.lowercased().contains($0) || item.url.lowercased().contains($0) }
        }
    }

    @ViewBuilder private var content: some View {
        let rows = items
        if rows.isEmpty {
            if !query.isEmpty {
                ContentUnavailableView.search(text: query)
            } else if filter == .unread, !library.readingList.isEmpty {
                ContentUnavailableView {
                    Label("All Caught Up", systemImage: "checkmark.circle")
                } description: {
                    Text("You have read everything on your list.")
                } actions: {
                    Button("Show All") { filter = .all }
                }
            } else {
                ContentUnavailableView("Your Reading List Is Empty", systemImage: "eyeglasses",
                                       description: Text("On any page, choose Bookmarks › Add to Reading List (⇧⌘D) to save it for later."))
            }
        } else {
            List(selection: $selection) {
                ForEach(rows) { item in
                    ReadingRow(item: item, library: library) { open(item, newTab: false) }
                        .tag(item.id)
                }
            }
            .listStyle(.inset)
            .contextMenu(forSelectionType: String.self) { ids in
                menu(ids)
            } primaryAction: { ids in
                for item in library.readingList where ids.contains(item.id) { open(item, newTab: ids.count > 1) }
            }
            .onDeleteCommand {
                for item in library.readingList where selection.contains(item.id) { library.removeFromReadingList(item) }
                selection = []
            }
            .accessibilityLabel("Reading List")
        }
    }

    @ViewBuilder
    private func menu(_ ids: Set<String>) -> some View {
        let chosen = library.readingList.filter { ids.contains($0.id) }
        if let item = chosen.first, chosen.count == 1 {
            Button("Open") { open(item, newTab: false) }
            Button("Open in New Tab") { open(item, newTab: true) }
            Divider()
            Button(item.isRead ? "Mark as Unread" : "Mark as Read") { library.setRead(item, !item.isRead) }
            Button("Copy Address") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(item.url, forType: .string)
            }
            Divider()
            Button("Remove") { library.removeFromReadingList(item) }
        } else if !chosen.isEmpty {
            Button("Mark \(chosen.count) as Read") { for item in chosen { library.setRead(item, true) } }
            Button("Mark \(chosen.count) as Unread") { for item in chosen { library.setRead(item, false) } }
            Divider()
            Button("Remove \(chosen.count) Items") { for item in chosen { library.removeFromReadingList(item) } }
        }
    }

    private func open(_ item: ReadingListItem, newTab: Bool) {
        library.setRead(item, true)
        libraryOpen(item.url, in: model, newTab: newTab)
    }
}

private struct ReadingRow: View {
    let item: ReadingListItem
    @ObservedObject var library: Library
    let open: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 12) {
            Button {
                library.setRead(item, !item.isRead)
            } label: {
                Image(systemName: item.isRead ? "circle" : "circle.fill")
                    .font(.system(size: 9))
                    .foregroundStyle(item.isRead ? Color.secondary : Color.accentColor)
                    .frame(width: 18, height: 18)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(item.isRead ? "Mark as Unread" : "Mark as Read")
            .accessibilityLabel(item.isRead ? "Mark as Unread" : "Mark as Read")

            SiteMonogram(url: item.url, size: 30)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.displayTitle)
                    .font(.system(size: 13, weight: item.isRead ? .regular : .semibold))
                    .foregroundStyle(item.isRead ? .secondary : .primary)
                    .lineLimit(2)
                Text(LibraryURL.host(of: item.url))
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 12)
            Text(item.added.libraryShortDescription)
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)
                .monospacedDigit()
            Button {
                library.removeFromReadingList(item)
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 10, weight: .semibold))
                    .frame(width: 20, height: 20)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .opacity(hovering ? 1 : 0)
            .help("Remove from Reading List")
            .accessibilityLabel("Remove from Reading List")
        }
        .padding(.vertical, 5)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .help(item.url)
    }
}
