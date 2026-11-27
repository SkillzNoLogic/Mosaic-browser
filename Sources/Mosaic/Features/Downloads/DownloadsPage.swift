import MosaicCore
import SwiftUI

/// Internal page `mosaic://downloads`: the whole download history, searchable.
/// Owner: **downloads**.
struct DownloadsPage: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        DownloadsPageContent(downloads: model.downloads, openSettings: { model.openSettings(.downloads) })
    }
}

private struct DownloadsPageContent: View {
    @ObservedObject var downloads: DownloadManager
    @ObservedObject var settings: DownloadSettings
    let openSettings: () -> Void
    @State private var selection: Set<UUID> = []

    init(downloads: DownloadManager, openSettings: @escaping () -> Void) {
        self.downloads = downloads
        self.settings = downloads.settings
        self.openSettings = openSettings
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text("Downloads").font(.title.weight(.semibold))
                HStack(spacing: 4) {
                    Text("Saving to \(settings.askWhereToSave ? "a place you choose each time" : settings.folderDisplayPath)")
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Button("Change…", action: openSettings)
                        .buttonStyle(.link)
                        .help("Settings › Downloads")
                }
                .font(.callout)
                .foregroundStyle(.secondary)
            }
            Spacer()
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Search downloads", text: $downloads.searchText)
                    .textFieldStyle(.plain)
                    .accessibilityLabel("Search downloads")
                if !downloads.searchText.isEmpty {
                    Button {
                        downloads.searchText = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Clear search")
                }
            }
            .padding(.horizontal, 8)
            .frame(width: 260, height: 28)
            .background(RoundedRectangle(cornerRadius: 7).fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(Color.primary.opacity(0.12)))
            Button("Clear List") { downloads.clearFinished() }
                .disabled(!downloads.items.contains { !$0.state.isActive })
                .help("Remove finished downloads from the list (files stay where they are)")
            Button {
                downloads.openFolder()
            } label: {
                Image(systemName: "folder")
            }
            .help("Open the download folder")
            .accessibilityLabel("Open the download folder")
        }
        .padding(.horizontal, 24)
        .padding(.vertical, 16)
    }

    @ViewBuilder private var content: some View {
        let records = downloads.filteredItems
        if downloads.items.isEmpty {
            ContentUnavailableView(
                "No Downloads", systemImage: "arrow.down.circle",
                description: Text("Files you download in Chromium or Mozilla tabs appear here."))
        } else if records.isEmpty {
            ContentUnavailableView.search(text: downloads.searchText)
        } else {
            List(selection: $selection) {
                ForEach(sections(records), id: \.title) { section in
                    Section(section.title) {
                        ForEach(section.records) { record in
                            DownloadRowView(downloads: downloads, record: record)
                                .tag(record.id)
                        }
                    }
                }
            }
            .listStyle(.inset)
            .contextMenu(forSelectionType: UUID.self) { ids in
                if ids.count == 1, let id = ids.first, let record = downloads.record(id) {
                    DownloadContextMenu(downloads: downloads, record: record)
                } else if !ids.isEmpty {
                    Button("Remove \(ids.count) Downloads from List") { ids.forEach(downloads.remove) }
                }
            } primaryAction: { ids in
                // Double-click or Return opens.
                for id in ids { downloads.open(id) }
            }
            .onDeleteCommand {
                selection.forEach(downloads.remove)
                selection.removeAll()
            }
        }
    }

    private struct DaySection {
        let title: String
        let records: [DownloadRecord]
    }

    private func sections(_ records: [DownloadRecord]) -> [DaySection] {
        let calendar = Calendar.current
        var order: [String] = []
        var grouped: [String: [DownloadRecord]] = [:]
        for record in records {
            let title: String
            if calendar.isDateInToday(record.startedAt) {
                title = "Today"
            } else if calendar.isDateInYesterday(record.startedAt) {
                title = "Yesterday"
            } else {
                title = record.startedAt.formatted(date: .complete, time: .omitted)
            }
            if grouped[title] == nil { order.append(title) }
            grouped[title, default: []].append(record)
        }
        return order.map { DaySection(title: $0, records: grouped[$0] ?? []) }
    }
}
