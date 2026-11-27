import AppKit
import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// Mount point: content of the new-tab page (Speed Dial) inside the shell's start page.
/// Owner: **productivity**. The shell places it (below its search field), with `BrowserModel`
/// in the environment; it paints no background, so the shell's background shows through,
/// and sizes itself to its content (max 760 pt wide).
struct StartPageContent: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        SpeedDialGrid(dial: model.productivity.speedDial, favicons: model.productivity.favicons, library: model.library)
    }
}

private struct SpeedDialGrid: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var dial: SpeedDialModel
    @ObservedObject var favicons: FaviconCache
    @ObservedObject var library: Library
    @State private var editing: EditTarget?
    @State private var dropTarget: UUID?

    enum EditTarget: Identifiable {
        case new
        case site(SpeedDialSite)
        var id: String {
            switch self {
            case .new: "new"
            case .site(let site): site.id.uuidString
            }
        }
    }

    private let columns = [GridItem(.adaptive(minimum: 96, maximum: 112), spacing: 14)]

    var body: some View {
        let tiles = dial.tiles(library: library)
        VStack(alignment: .leading, spacing: 10) {
            header(hasHidden: !dial.dial.hiddenTopSites.isEmpty)
            LazyVGrid(columns: columns, spacing: 16) {
                ForEach(Array(tiles.pinned.enumerated()), id: \.element.id) { index, site in
                    tile(site, pinned: true, index: index, count: tiles.pinned.count)
                        .draggable(site.id.uuidString)
                        .dropDestination(for: String.self) { items, _ in
                            guard let raw = items.first, let id = UUID(uuidString: raw) else { return false }
                            dial.move(id, to: index)
                            return true
                        } isTargeted: { dropTarget = $0 ? site.id : (dropTarget == site.id ? nil : dropTarget) }
                }
                ForEach(tiles.topSites) { site in
                    tile(site, pinned: false, index: 0, count: 0)
                }
                addTile
            }
            if let error = dial.saveError {
                Label(error, systemImage: "exclamationmark.triangle").font(.callout).foregroundStyle(.red)
            }
        }
        .frame(maxWidth: 760)
        .padding(.horizontal, 24)
        .popover(item: $editing, arrowEdge: .bottom) { target in
            SiteEditor(target: target, dial: dial) { editing = nil }
        }
    }

    private func header(hasHidden: Bool) -> some View {
        HStack {
            Text("Speed Dial")
                .font(.headline)
                .foregroundStyle(.secondary)
                .accessibilityAddTraits(.isHeader)
            Spacer()
            Menu {
                Button("Add Site…") { editing = .new }
                if let tab = model.selectedTab, !tab.isStartPage, tab.internalPage == nil {
                    Button("Add Current Page") { _ = try? dial.add(title: tab.title, url: tab.url) }
                }
                Divider()
                Toggle("Show Most Visited Sites", isOn: Binding(get: { dial.dial.showsTopSites }, set: { dial.setShowsTopSites($0) }))
                Toggle("Show Site Icons", isOn: Binding(get: { dial.dial.showsIcons }, set: { dial.setShowsIcons($0) }))
                if hasHidden {
                    Button("Restore Removed Sites") { dial.restoreHiddenTopSites() }
                }
                Divider()
                Text("Icons are fetched directly from each site, without cookies, and kept in your profile.")
            } label: {
                Image(systemName: "ellipsis.circle")
                    .font(.system(size: 15))
                    .frame(width: 28, height: 28)
                    .contentShape(Rectangle())
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("Speed Dial options")
            .accessibilityLabel("Speed Dial options")
        }
    }

    private func tile(_ site: SpeedDialSite, pinned: Bool, index: Int, count: Int) -> some View {
        SpeedDialTile(
            site: site, pinned: pinned,
            icon: dial.dial.showsIcons ? favicons.image(for: site.url, fetch: true) : nil,
            isDropTarget: dropTarget == site.id
        ) { newTab in
            if newTab { model.newTab(url: site.url) } else { model.navigate(site.url) }
        }
        .contextMenu {
            Button("Open in New Tab") { model.newTab(url: site.url) }
            Divider()
            if pinned {
                Button("Edit…") { editing = .site(site) }
                Button("Move Left") { dial.shift(site.id, by: -1) }.disabled(index == 0)
                Button("Move Right") { dial.shift(site.id, by: 1) }.disabled(index >= count - 1)
                Divider()
                Button("Remove", role: .destructive) { dial.remove(site.id) }
            } else {
                Button("Pin to Speed Dial") { dial.pin(site) }
                Button("Remove", role: .destructive) { dial.hideTopSite(site) }
            }
        }
        .accessibilityActions {
            if pinned {
                Button("Edit") { editing = .site(site) }
                if index > 0 { Button("Move left") { dial.shift(site.id, by: -1) } }
                if index < count - 1 { Button("Move right") { dial.shift(site.id, by: 1) } }
                Button("Remove") { dial.remove(site.id) }
            } else {
                Button("Pin to Speed Dial") { dial.pin(site) }
                Button("Remove") { dial.hideTopSite(site) }
            }
        }
    }

    private var addTile: some View {
        Button {
            editing = .new
        } label: {
            VStack(spacing: 8) {
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.25), style: StrokeStyle(lineWidth: 1.5, dash: [5, 4]))
                    .frame(width: 64, height: 64)
                    .overlay(Image(systemName: "plus").font(.system(size: 20, weight: .medium)).foregroundStyle(.secondary))
                Text("Add Site").font(.system(size: 12)).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Add a site to Speed Dial")
        .accessibilityLabel("Add site to Speed Dial")
    }
}

private struct SpeedDialTile: View {
    let site: SpeedDialSite
    let pinned: Bool
    let icon: NSImage?
    let isDropTarget: Bool
    let open: (_ newTab: Bool) -> Void
    @State private var hovering = false
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    var body: some View {
        Button {
            open(NSEvent.modifierFlags.contains(.command))
        } label: {
            VStack(spacing: 8) {
                ZStack {
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .fill(reduceTransparency ? AnyShapeStyle(Color(nsColor: .controlBackgroundColor)) : AnyShapeStyle(.regularMaterial))
                    if let icon {
                        Image(nsImage: icon)
                            .resizable()
                            .interpolation(.high)
                            .aspectRatio(contentMode: .fit)
                            .frame(width: 34, height: 34)
                            .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
                    } else {
                        LetterTile(site: site)
                    }
                }
                .frame(width: 64, height: 64)
                .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(isDropTarget ? Color.accentColor : Color.primary.opacity(hovering ? 0.25 : 0.1),
                                  lineWidth: isDropTarget ? 2 : 1))
                .shadow(color: .black.opacity(hovering ? 0.18 : 0.08), radius: hovering ? 8 : 4, y: 2)
                HStack(spacing: 3) {
                    if pinned {
                        Image(systemName: "pin.fill").font(.system(size: 8)).foregroundStyle(.secondary)
                            .accessibilityHidden(true)
                    }
                    Text(site.displayTitle)
                        .font(.system(size: 12))
                        .lineLimit(1)
                        .truncationMode(.tail)
                }
                .frame(maxWidth: 104)
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help("\(site.displayTitle)\n\(site.url)")
        .accessibilityLabel(site.displayTitle)
        .accessibilityHint(pinned ? "Pinned site. Opens \(site.host)" : "Frequently visited. Opens \(site.host)")
    }
}

/// The site's initial on a colour derived from its host, when there is no icon.
private struct LetterTile: View {
    let site: SpeedDialSite

    var body: some View {
        let letter = site.displayTitle.first.map { String($0).uppercased() } ?? "?"
        Text(letter)
            .font(.system(size: 22, weight: .semibold, design: .rounded))
            .foregroundStyle(.white)
            .frame(width: 38, height: 38)
            .background(Circle().fill(color))
            .accessibilityHidden(true)
    }

    private var color: Color {
        let hash = site.host.unicodeScalars.reduce(UInt32(5381)) { ($0 &* 33) &+ $1.value }
        return Color(hue: Double(hash % 360) / 360, saturation: 0.5, brightness: 0.75)
    }
}

/// Add or edit a pinned site.
private struct SiteEditor: View {
    let target: SpeedDialGrid.EditTarget
    @ObservedObject var dial: SpeedDialModel
    let done: () -> Void
    @State private var title = ""
    @State private var url = ""
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(isNew ? "Add to Speed Dial" : "Edit Site").font(.headline)
            Form {
                TextField("Name", text: $title, prompt: Text("Optional"))
                TextField("Address", text: $url, prompt: Text("example.com"))
                    .onSubmit(save)
            }
            .formStyle(.columns)
            if let error {
                Text(error).font(.callout).foregroundStyle(.red)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: done).keyboardShortcut(.cancelAction)
                Button(isNew ? "Add" : "Save", action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(url.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(16)
        .frame(width: 340)
        .onAppear {
            if case .site(let site) = target {
                title = site.title
                url = site.url
            }
        }
    }

    private var isNew: Bool {
        if case .new = target { return true }
        return false
    }

    private func save() {
        do {
            switch target {
            case .new: try dial.add(title: title, url: url)
            case .site(let site): try dial.update(site.id, title: title, url: url)
            }
            done()
        } catch {
            self.error = SpeedDialModel.message(for: error)
        }
    }
}
