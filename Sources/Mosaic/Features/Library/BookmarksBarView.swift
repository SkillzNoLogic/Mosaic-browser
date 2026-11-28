import AppKit
import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// Mount point: the bookmarks bar shown under the toolbar (when the Mosaic has one).
/// Owner: **library**. The shell places this view (full width, its own height of
/// `BookmarksBarView.height`) and draws the background; it is empty while the bar is hidden
/// (View › Show Bookmarks Bar, ⇧⌘B).
struct BookmarksBarView: View {
    @EnvironmentObject private var model: BrowserModel

    static let height: CGFloat = 30

    var body: some View {
        BookmarksBarContent(library: model.library)
    }
}

private struct BookmarksBarContent: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var library: Library
    @State private var editing: String?
    @State private var dropTarget: String?

    var body: some View {
        if library.showsBookmarksBar {
            GeometryReader { proxy in
                bar(width: proxy.size.width)
            }
            .frame(height: BookmarksBarView.height)
            .sheet(item: Binding(get: { editing.map(EditTarget.init) }, set: { editing = $0?.id })) { target in
                BookmarkEditor(library: library, bookmarkID: target.id) { editing = nil }
            }
        }
    }

    private struct EditTarget: Identifiable { let id: String }

    @ViewBuilder
    private func bar(width: CGFloat) -> some View {
        let items = library.bookmarksBar
        let other = library.children(of: Bookmark.otherID)
        let trailingWidth: CGFloat = other.isEmpty ? 0 : BarMetrics.width(of: "Other Bookmarks", folder: true) + 8
        let layout = BarMetrics.fit(items, in: width - 20 - trailingWidth)

        HStack(spacing: 2) {
            if items.isEmpty {
                emptyHint
            }
            ForEach(layout.visible) { item in
                BarItem(item: item, library: library, isDropTarget: dropTarget == item.id,
                        edit: { editing = item.id })
                    .onDrop(of: [.plainText, .url], isTargeted: targetBinding(item.id)) { providers in
                        drop(providers, before: item)
                    }
            }
            if !layout.overflow.isEmpty {
                Menu {
                    BookmarkMenuItems(items: layout.overflow, library: library)
                } label: {
                    Image(systemName: "chevron.right.2")
                        .font(.system(size: 11, weight: .semibold))
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                .padding(.horizontal, 6)
                .help("More bookmarks")
                .accessibilityLabel("More bookmarks")
            }
            Spacer(minLength: 0)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .contentShape(Rectangle())
                .onDrop(of: [.plainText, .url], isTargeted: nil) { providers in drop(providers, before: nil) }
            if !other.isEmpty, let folder = library.bookmark(id: Bookmark.otherID) {
                Divider().frame(height: 16)
                BarItem(item: folder, library: library, isDropTarget: false, edit: nil)
            }
        }
        .padding(.horizontal, 10)
        .frame(width: width, height: BookmarksBarView.height)
        .contextMenu { barMenu }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Bookmarks bar")
    }

    private var emptyHint: some View {
        HStack(spacing: 6) {
            Text("For quick access, add bookmarks here with ⌘D.")
                .foregroundStyle(.secondary)
            Button("Import bookmarks…") { LibraryCommands.importBookmarks(model: model) }
                .buttonStyle(.link)
        }
        .font(.system(size: 12))
        .padding(.horizontal, 4)
        .lineLimit(1)
    }

    @ViewBuilder private var barMenu: some View {
        if let tab = model.selectedTab, LibraryURL.isRecordable(tab.url) {
            Button("Add This Page to the Bar") {
                library.addBookmark(url: tab.url, title: tab.displayTitle, folderID: Bookmark.barID)
            }
        }
        Button("New Folder…") {
            if let folder = library.addFolder(title: "New Folder", in: Bookmark.barID) { editing = folder.id }
        }
        Divider()
        Button("Show All Bookmarks") { model.openInternalPage(.bookmarks) }
        Button("Hide Bookmarks Bar") { library.showsBookmarksBar = false }
    }

    private func targetBinding(_ id: String) -> Binding<Bool> {
        Binding(get: { dropTarget == id }, set: { dropTarget = $0 ? id : (dropTarget == id ? nil : dropTarget) })
    }

    /// A bookmark dragged within the bar moves before `target`; an address dragged in from a
    /// page becomes a new bookmark there.
    private func drop(_ providers: [NSItemProvider], before target: Bookmark?) -> Bool {
        guard let provider = providers.first else { return false }
        let position = target?.position
        if provider.canLoadObject(ofClass: NSString.self) {
            _ = provider.loadObject(ofClass: NSString.self) { object, _ in
                guard let text = object as? String else { return }
                Task { @MainActor in
                    if let id = BookmarkDrag.id(from: text) {
                        library.moveBookmark(id: id, to: Bookmark.barID, position: position)
                    } else if let url = URL(string: text), url.scheme?.hasPrefix("http") == true {
                        _ = library.addBookmark(url: text, title: "", folderID: Bookmark.barID)
                        if let position, let added = library.bookmarksBar.last {
                            library.moveBookmark(id: added.id, to: Bookmark.barID, position: position)
                        }
                    }
                }
            }
            return true
        }
        if provider.canLoadObject(ofClass: URL.self) {
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                guard let url, url.scheme?.hasPrefix("http") == true else { return }
                Task { @MainActor in
                    _ = library.addBookmark(url: url.absoluteString, title: "", folderID: Bookmark.barID)
                }
            }
            return true
        }
        return false
    }
}

/// One button (or folder menu) on the bar.
private struct BarItem: View {
    @EnvironmentObject private var model: BrowserModel
    let item: Bookmark
    @ObservedObject var library: Library
    let isDropTarget: Bool
    let edit: (() -> Void)?
    @State private var hovering = false

    var body: some View {
        Group {
            if item.isFolder {
                Menu {
                    let children = library.children(of: item.id)
                    if children.isEmpty {
                        Text("Empty")
                    } else {
                        BookmarkMenuItems(items: children, library: library)
                        Divider()
                        Button("Open All in New Tabs") {
                            for child in children { if let url = child.url { model.newTab(url: url, select: false) } }
                        }
                    }
                } label: {
                    label
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
            } else {
                Button {
                    if let url = item.url { libraryOpen(url, in: model) }
                } label: {
                    label
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 7)
        .frame(height: 24)
        .background(
            RoundedRectangle(cornerRadius: 6)
                .fill(Color.primary.opacity(isDropTarget ? 0.16 : (hovering ? 0.08 : 0)))
        )
        .overlay(alignment: .leading) {
            if isDropTarget {
                Capsule().fill(Color.accentColor).frame(width: 2, height: 18).offset(x: -2)
            }
        }
        .contentShape(RoundedRectangle(cornerRadius: 6))
        .onHover { hovering = $0 }
        .help(item.isFolder ? item.displayTitle : "\(item.displayTitle)\n\(item.url ?? "")")
        .onDrag { NSItemProvider(object: BookmarkDrag.payload(item.id) as NSString) }
        .contextMenu { menu }
        .accessibilityLabel(item.isFolder ? "\(item.displayTitle) folder" : item.displayTitle)
    }

    private var label: some View {
        HStack(spacing: 5) {
            if item.isFolder {
                Image(systemName: "folder")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Text(item.displayTitle)
                .font(.system(size: 12))
                .lineLimit(1)
                .truncationMode(.tail)
                .frame(maxWidth: BarMetrics.maxTitleWidth, alignment: .leading)
                .fixedSize(horizontal: true, vertical: false)
        }
        .foregroundStyle(.primary)
    }

    @ViewBuilder private var menu: some View {
        if let url = item.url {
            Button("Open") { libraryOpen(url, in: model) }
            Button("Open in New Tab") { libraryOpen(url, in: model, newTab: true) }
            Divider()
            Button("Copy Address") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(url, forType: .string)
            }
        }
        if let edit {
            Button(item.isFolder ? "Rename…" : "Edit…", action: edit)
            Divider()
            Button("Delete", role: .destructive) { library.deleteBookmarks([item.id]) }
        }
    }
}

/// Bookmarks as menu items, folders as submenus (the bar's folders, its overflow menu).
struct BookmarkMenuItems: View {
    @EnvironmentObject private var model: BrowserModel
    let items: [Bookmark]
    @ObservedObject var library: Library

    var body: some View {
        ForEach(items) { item in
            if item.isFolder {
                Menu(item.displayTitle) {
                    let children = library.children(of: item.id)
                    if children.isEmpty { Text("Empty") } else { BookmarkMenuItems(items: children, library: library) }
                }
            } else if let url = item.url {
                Button(item.displayTitle) { libraryOpen(url, in: model) }
            }
        }
    }
}

/// Measures bar items so the ones that do not fit go into the overflow menu.
enum BarMetrics {
    static let maxTitleWidth: CGFloat = 150
    private static let font = NSFont.systemFont(ofSize: 12)

    static func width(of title: String, folder: Bool) -> CGFloat {
        let text = min(ceil((title as NSString).size(withAttributes: [.font: font]).width), maxTitleWidth)
        return text + 14 + (folder ? 16 : 0) + 2
    }

    static func fit(_ items: [Bookmark], in available: CGFloat) -> (visible: [Bookmark], overflow: [Bookmark]) {
        let widths = items.map { width(of: $0.displayTitle, folder: $0.isFolder) }
        guard widths.reduce(0, +) > available else { return (items, []) }
        let room = available - 30  // the overflow button
        var used: CGFloat = 0
        var count = 0
        for width in widths {
            guard used + width <= room else { break }
            used += width
            count += 1
        }
        return (Array(items.prefix(count)), Array(items.dropFirst(count)))
    }
}
