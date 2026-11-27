import AppKit
import MosaicCore
import SwiftUI

/// Edits one bookmark (name, address, folder, tags) or folder (name, parent). Used by the
/// Bookmarks page, the bookmarks bar and Bookmark This Page (⌘D); the shell may also show it
/// in a popover from the address bar's star.
struct BookmarkEditor: View {
    @ObservedObject var library: Library
    let bookmarkID: String
    /// Heading; "Bookmark Added" after ⌘D, "Edit Bookmark" otherwise.
    var heading: String?
    var onDone: () -> Void

    @State private var title = ""
    @State private var address = ""
    @State private var folderID = Bookmark.barID
    @State private var tags = ""
    @State private var loaded = false
    @FocusState private var titleFocused: Bool

    private var bookmark: Bookmark? { library.bookmark(id: bookmarkID) }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if let bookmark {
                Text(heading ?? (bookmark.isFolder ? "Edit Folder" : "Edit Bookmark"))
                    .font(.headline)
                Form {
                    TextField("Name", text: $title)
                        .focused($titleFocused)
                    if !bookmark.isFolder {
                        TextField("Address", text: $address)
                    }
                    Picker("Folder", selection: $folderID) {
                        ForEach(folderChoices(excluding: bookmark), id: \.folder.id) { entry in
                            Text(String(repeating: "    ", count: entry.depth) + entry.folder.displayTitle)
                                .tag(entry.folder.id)
                        }
                    }
                    if !bookmark.isFolder {
                        TextField("Tags", text: $tags, prompt: Text("Separate with commas"))
                    }
                }
                .formStyle(.columns)
                if let problem {
                    Text(problem).font(.callout).foregroundStyle(.red)
                }
                HStack {
                    Button(bookmark.isFolder ? "Delete Folder" : "Remove Bookmark", role: .destructive) {
                        library.deleteBookmarks([bookmarkID])
                        onDone()
                    }
                    Spacer()
                    Button("Cancel", role: .cancel, action: onDone)
                        .keyboardShortcut(.cancelAction)
                    Button("Save", action: save)
                        .keyboardShortcut(.defaultAction)
                        .disabled(problem != nil)
                }
            } else {
                Text("This bookmark no longer exists.").foregroundStyle(.secondary)
                HStack { Spacer(); Button("Close", action: onDone).keyboardShortcut(.defaultAction) }
            }
        }
        .padding(20)
        .frame(width: 440)
        .onAppear(perform: load)
    }

    private var problem: String? {
        guard let bookmark, !bookmark.isFolder else { return nil }
        if address.trimmingCharacters(in: .whitespaces).isEmpty { return "Enter an address." }
        return nil
    }

    /// Folders it may move into: everything but itself and, for a folder, its own subfolders.
    private func folderChoices(excluding bookmark: Bookmark) -> [(folder: Bookmark, depth: Int)] {
        let tree = library.folderTree()
        guard bookmark.isFolder else { return tree }
        var result: [(folder: Bookmark, depth: Int)] = []
        var skippingBelow: Int?
        for entry in tree {
            if let depth = skippingBelow {
                if entry.depth > depth { continue }
                skippingBelow = nil
            }
            if entry.folder.id == bookmark.id {
                skippingBelow = entry.depth
                continue
            }
            result.append(entry)
        }
        return result
    }

    private func load() {
        guard !loaded, let bookmark else { return }
        loaded = true
        title = bookmark.title
        address = bookmark.url ?? ""
        folderID = bookmark.parentID ?? Bookmark.barID
        tags = bookmark.tags.joined(separator: ", ")
        titleFocused = true
    }

    private func save() {
        guard let bookmark else { return onDone() }
        var url: String?
        if !bookmark.isFolder {
            let text = address.trimmingCharacters(in: .whitespacesAndNewlines)
            url = URL(string: text)?.scheme == nil
                ? (AddressResolver.resolve(text, searchEngine: .default)?.absoluteString ?? text) : text
        }
        let tagList = bookmark.isFolder ? nil : tags.split(separator: ",").map(String.init)
        library.updateBookmark(id: bookmarkID, title: title.trimmingCharacters(in: .whitespaces), url: url,
                               tags: tagList, folderID: bookmark.isRoot ? nil : folderID)
        onDone()
    }
}

/// Asks for a new folder's name.
struct NewFolderSheet: View {
    @ObservedObject var library: Library
    let parentID: String
    var onDone: (Bookmark?) -> Void

    @State private var name = ""
    @State private var parent = Bookmark.otherID

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("New Folder").font(.headline)
            Form {
                TextField("Name", text: $name, prompt: Text("Folder name"))
                Picker("In", selection: $parent) {
                    ForEach(library.folderTree(), id: \.folder.id) { entry in
                        Text(String(repeating: "    ", count: entry.depth) + entry.folder.displayTitle)
                            .tag(entry.folder.id)
                    }
                }
            }
            .formStyle(.columns)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { onDone(nil) }
                    .keyboardShortcut(.cancelAction)
                Button("Create") {
                    onDone(library.addFolder(title: name.trimmingCharacters(in: .whitespaces), in: parent))
                }
                .keyboardShortcut(.defaultAction)
                .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(20)
        .frame(width: 380)
        .onAppear { parent = parentID }
    }
}

/// Presents SwiftUI content as a sheet on Mosaic's window (used from menu commands).
@MainActor
enum LibrarySheet {
    static func present<Content: View>(on window: NSWindow?, _ content: (@escaping () -> Void) -> Content) {
        guard let window, window.attachedSheet == nil else { return }
        let sheet = NSWindow(contentRect: .zero, styleMask: [.titled], backing: .buffered, defer: true)
        let close = { [weak window, weak sheet] in
            guard let window, let sheet else { return }
            window.endSheet(sheet)
        }
        sheet.contentViewController = NSHostingController(rootView: content(close))
        window.beginSheet(sheet)
    }
}
