import AppKit
import MosaicCore
import SwiftUI

/// Internal page `mosaic://history`. Owner: **library**.
///
/// Visits grouped by day, newest first, with search, deleting single visits or a whole
/// site, and clearing the last hour, today or everything.
struct HistoryPage: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        HistoryBrowser(library: model.library)
            .libraryPageBackground()
    }
}

private struct HistoryBrowser: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var library: Library

    @State private var query = ""
    @State private var visits: [HistoryVisit] = []
    @State private var canLoadMore = false
    @State private var isLoading = false
    @State private var selection: Set<Int64> = []
    @State private var clearRange: Library.ClearRange?
    @State private var siteToDelete: String?
    @State private var counts = (addresses: 0, visits: 0)

    private static let pageSize = 300

    var body: some View {
        VStack(spacing: 0) {
            LibraryPageHeader(title: "History", subtitle: subtitle) {
                LibrarySearchField(prompt: "Search history", text: $query)
                Menu {
                    ForEach(Library.ClearRange.allCases) { range in
                        Button("Clear \(range.title)…") { clearRange = range }
                    }
                } label: {
                    Text("Clear History")
                }
                .fixedSize()
                .disabled(counts.visits == 0)
                .help("Delete the history of the last hour, today, or all of it")
            }
            .controlSize(.large)
            LibraryStorageWarning(message: library.storageProblem)
            Divider()
            content
        }
        .task(id: Reload(query: query, revision: library.historyRevision)) {
            // Typing in the search field: wait for a pause before querying.
            if !query.isEmpty { try? await Task.sleep(nanoseconds: 150_000_000) }
            guard !Task.isCancelled else { return }
            await load(reset: true)
        }
        .confirmationDialog(clearQuestion, isPresented: Binding(
            get: { clearRange != nil }, set: { if !$0 { clearRange = nil } })
        ) {
            Button("Clear", role: .destructive) {
                if let clearRange { library.clearHistory(clearRange) }
                clearRange = nil
            }
        } message: {
            Text("Bookmarks and the Reading List are not affected. This cannot be undone.")
        }
        .confirmationDialog("Delete all history from \(siteToDelete ?? "")?", isPresented: Binding(
            get: { siteToDelete != nil }, set: { if !$0 { siteToDelete = nil } })
        ) {
            Button("Delete", role: .destructive) {
                if let siteToDelete { library.deleteHistory(site: siteToDelete) }
                siteToDelete = nil
            }
        } message: {
            Text("Every visit to this site and its subdomains is removed.")
        }
    }

    private struct Reload: Equatable {
        let query: String
        let revision: Int
    }

    private var subtitle: String {
        let pages = counts.addresses == 1 ? "1 page" : "\(counts.addresses) pages"
        let visits = counts.visits == 1 ? "1 visit" : "\(counts.visits) visits"
        return "\(pages) · \(visits)"
    }

    private var clearQuestion: String {
        switch clearRange {
        case .lastHour: "Clear the last hour of history?"
        case .today: "Clear today’s history?"
        case .all, nil: "Clear all history?"
        }
    }

    // MARK: Loading

    private func load(reset: Bool) async {
        guard !isLoading || reset else { return }
        isLoading = true
        defer { isLoading = false }
        let store = library.store
        let text = query
        let before = reset ? nil : visits.last?.time
        let limit = Self.pageSize
        let (page, totals) = await Task.detached(priority: .userInitiated) {
            (store.visits(matching: text, before: before, limit: limit), store.historyCounts())
        }.value
        guard text == query else { return }
        if reset {
            visits = page
            selection = selection.filter { id in page.contains { $0.id == id } }
        } else {
            visits += page
        }
        counts = totals
        canLoadMore = page.count == limit
    }

    // MARK: Content

    private var days: [(day: Date, visits: [HistoryVisit])] {
        let calendar = Calendar.current
        var result: [(Date, [HistoryVisit])] = []
        for visit in visits {
            let day = calendar.startOfDay(for: visit.time)
            if let last = result.last, last.0 == day {
                result[result.count - 1].1.append(visit)
            } else {
                result.append((day, [visit]))
            }
        }
        return result
    }

    @ViewBuilder private var content: some View {
        if visits.isEmpty {
            if !query.isEmpty {
                ContentUnavailableView.search(text: query)
            } else if !isLoading {
                ContentUnavailableView("No History", systemImage: "clock",
                                       description: Text("Pages you visit in either engine appear here."))
            } else {
                Color.clear
            }
        } else {
            List(selection: $selection) {
                ForEach(days, id: \.day) { group in
                    Section {
                        ForEach(group.visits) { visit in
                            HistoryRow(visit: visit) { selection = [visit.id]; deleteSelected() }
                                .tag(visit.id)
                                .onAppear {
                                    if visit.id == visits.last?.id, canLoadMore { Task { await load(reset: false) } }
                                }
                        }
                    } header: {
                        Text(Self.heading(for: group.day))
                            .font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .listStyle(.inset)
            .contextMenu(forSelectionType: Int64.self) { ids in
                menu(ids)
            } primaryAction: { ids in
                let chosen = visits.filter { ids.contains($0.id) }
                if chosen.count == 1 { libraryOpen(chosen[0].url, in: model) }
                else { for visit in chosen { model.newTab(url: visit.url, select: false) } }
            }
            .onDeleteCommand(perform: deleteSelected)
            .accessibilityLabel("History")
        }
    }

    @ViewBuilder
    private func menu(_ ids: Set<Int64>) -> some View {
        let chosen = visits.filter { ids.contains($0.id) }
        if chosen.count == 1, let visit = chosen.first {
            Button("Open") { libraryOpen(visit.url, in: model) }
            Button("Open in New Tab") { libraryOpen(visit.url, in: model, newTab: true) }
            Divider()
            Button("Copy Address") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(visit.url, forType: .string)
            }
            Button(library.isBookmarked(visit.url) ? "Bookmarked" : "Bookmark") {
                library.addBookmark(url: visit.url, title: visit.title)
            }
            .disabled(library.isBookmarked(visit.url))
            if !visit.host.isEmpty {
                Button("More from \(visit.host)") { query = visit.host }
            }
            Divider()
            Button("Delete") { selection = [visit.id]; deleteSelected() }
            if !visit.host.isEmpty {
                Button("Delete All from \(visit.host)…") { siteToDelete = visit.host }
            }
        } else if !chosen.isEmpty {
            Button("Open \(chosen.count) in New Tabs") {
                for visit in chosen { model.newTab(url: visit.url, select: false) }
            }
            Divider()
            Button("Delete \(chosen.count) Visits") { selection = ids; deleteSelected() }
        }
    }

    private func deleteSelected() {
        guard !selection.isEmpty else { return }
        library.deleteVisits(Array(selection))
        visits.removeAll { selection.contains($0.id) }
        selection = []
    }

    static func heading(for day: Date) -> String {
        let calendar = Calendar.current
        let date = day.formatted(.dateTime.weekday(.wide).day().month(.wide))
        if calendar.isDateInToday(day) { return "Today — \(date)" }
        if calendar.isDateInYesterday(day) { return "Yesterday — \(date)" }
        if calendar.isDate(day, equalTo: Date(), toGranularity: .year) { return date }
        return day.formatted(.dateTime.weekday(.wide).day().month(.wide).year())
    }
}

private struct HistoryRow: View {
    let visit: HistoryVisit
    let delete: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 12) {
            Text(visit.time.formatted(date: .omitted, time: .shortened))
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .monospacedDigit()
                .frame(width: 58, alignment: .leading)
            SiteMonogram(url: visit.url, size: 20)
            Text(visit.displayTitle)
                .font(.system(size: 13))
                .lineLimit(1)
                .layoutPriority(1)
            Text(visit.host)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .lineLimit(1)
            Spacer(minLength: 8)
            if visit.typed {
                Image(systemName: "keyboard")
                    .font(.system(size: 10))
                    .foregroundStyle(.tertiary)
                    .help("Typed in the address bar")
                    .accessibilityLabel("Typed")
            }
            Button(action: delete) {
                Image(systemName: "xmark")
                    .font(.system(size: 10, weight: .semibold))
                    .frame(width: 20, height: 20)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .opacity(hovering ? 1 : 0)
            .help("Delete this visit")
            .accessibilityLabel("Delete visit")
        }
        .padding(.vertical, 3)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .help(visit.url)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(visit.displayTitle), \(visit.host), \(visit.time.formatted(date: .omitted, time: .shortened))")
        .accessibilityAction(named: "Delete", delete)
    }
}
