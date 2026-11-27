import MosaicCore
import SwiftUI

/// Toolbar button for the built-in content blocker: a shield showing whether
/// the page in front is being filtered and how much was blocked on it.
struct BlockerButton: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        let tab = model.selectedTab
        let filtering = tab.map { model.blocker.isFiltering(pageURL: $0.url) } ?? false
        let count = tab?.blockedCount ?? 0
        Button {
            model.showBlocker.toggle()
        } label: {
            HStack(spacing: 3) {
                Image(systemName: symbol(filtering: filtering))
                if filtering, count > 0 {
                    Text(count > 999 ? "999+" : "\(count)")
                        .font(.system(size: 11, weight: .medium))
                        .monospacedDigit()
                }
            }
        }
        .help(helpText(filtering: filtering, count: count))
        .accessibilityLabel("Content blocker")
        .accessibilityValue(helpText(filtering: filtering, count: count))
        .popover(isPresented: $model.showBlocker, arrowEdge: .bottom) {
            BlockerPopover().environmentObject(model)
        }
    }

    private func symbol(filtering: Bool) -> String {
        if model.blocker.status == .unavailable || !model.blocker.isEnabled { return "shield.slash" }
        return filtering ? "shield.lefthalf.filled" : "shield"
    }

    private func helpText(filtering: Bool, count: Int) -> String {
        if model.blocker.status == .unavailable { return "Content blocker is not included in this build" }
        if !model.blocker.isEnabled { return "Content blocker is off" }
        if !filtering { return "Content blocker is not active on this page" }
        return count == 1 ? "1 request blocked on this page" : "\(count) requests blocked on this page"
    }
}

private struct BlockerPopover: View {
    @EnvironmentObject private var model: BrowserModel
    @State private var requests: [ContentBlocker.BlockedRequest] = []
    @State private var showDetails = true

    var body: some View {
        let blocker = model.blocker
        let tab = model.selectedTab
        let site = tab.flatMap { blocker.site(ofPage: $0.url) }

        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text("Content Blocker").font(.headline)
                Text("Built in. Uses uBlock Origin’s filtering engine and filter lists.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if blocker.status == .unavailable {
                Label("Not included in this build. Run Scripts/bootstrap.sh and rebuild.",
                      systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Toggle("Block ads and trackers", isOn: Binding(
                    get: { blocker.isEnabled },
                    set: { model.setBlockerEnabled($0) }
                ))
                .toggleStyle(.switch)

                if let tab, let site {
                    Toggle("On for \(site)", isOn: Binding(
                        get: { !blocker.settings.isAllowlisted(site: site) },
                        set: { model.setBlocking($0, onSiteOf: tab) }
                    ))
                    .toggleStyle(.switch)
                    .disabled(!blocker.isEnabled)
                    .help("Turn the blocker off for \(site) if the page does not work")

                    if blocker.isFiltering(pageURL: tab.url) {
                        dashboard(tab)
                    } else if blocker.isEnabled {
                        troubleshootingAfterAllowing(site)
                    }
                }

                Divider()

                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(statusLine).font(.caption)
                        Text(listsLine).font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if blocker.isUpdating {
                        ProgressView().controlSize(.small)
                    } else {
                        Button("Update Lists") { Task { await blocker.updateLists() } }
                            .controlSize(.small)
                    }
                }

                HStack {
                    Text(blocker.cosmeticEnabled
                         ? "Also hides leftover ad elements. Scriptlets are not supported, so some video ads can still appear."
                         : "Element hiding is off; empty ad spaces may remain.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 8)
                    Button("Settings…") {
                        model.showBlocker = false
                        model.openSettings(.privacy)
                    }
                    .controlSize(.small)
                    .help("Filter lists, My filters and sites where the blocker is off")
                }
            }
        }
        .padding(14)
        .frame(width: 380)
    }

    /// What was blocked on this page, and by which filter.
    @ViewBuilder private func dashboard(_ tab: BrowserTab) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Button {
                showDetails.toggle()
            } label: {
                HStack {
                    Image(systemName: "chevron.right")
                        .rotationEffect(.degrees(showDetails ? 90 : 0))
                        .font(.caption.weight(.semibold))
                        .accessibilityHidden(true)
                    Text(tab.blockedCount == 1
                         ? "1 request blocked on this page"
                         : "\(tab.blockedCount) requests blocked on this page")
                        .font(.callout)
                    Spacer()
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(tab.blockedCount == 0)
            .accessibilityHint("Shows which requests were blocked and by which filter")

            if showDetails, tab.blockedCount > 0 {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 6) {
                        ForEach(requests) { request in
                            VStack(alignment: .leading, spacing: 1) {
                                HStack(spacing: 4) {
                                    Text(request.host).font(.callout.weight(.medium)).lineLimit(1)
                                    Text(request.type).font(.caption2).padding(.horizontal, 4)
                                        .background(.quaternary, in: Capsule())
                                    if request.count > 1 {
                                        Text("×\(request.count)").font(.caption2).foregroundStyle(.secondary)
                                    }
                                }
                                Text(request.url).font(.caption2).foregroundStyle(.secondary)
                                    .lineLimit(1).truncationMode(.middle)
                                    .help(request.url)
                                if !request.filter.isEmpty {
                                    Text(request.filter + (request.list.map { "  ·  \($0)" } ?? ""))
                                        .font(.caption2.monospaced())
                                        .foregroundStyle(.orange)
                                        .lineLimit(1).truncationMode(.tail)
                                        .help("Blocked by \(request.filter)" + (request.list.map { " (\($0))" } ?? ""))
                                }
                            }
                            .accessibilityElement(children: .combine)
                        }
                    }
                    .padding(.vertical, 2)
                }
                .frame(maxHeight: 220)
                .task(id: tab.blockedCount) {
                    requests = await model.blocker.blockedRequests(in: tab)
                }
            }
            Label("Page not working? Switch the blocker off for this site above; the page reloads without filtering. If that fixes it, a filter was in the way.",
                  systemImage: "lightbulb")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func troubleshootingAfterAllowing(_ site: String) -> some View {
        Text("The blocker is off for \(site). If the page works now and you want the blocker back, turn it on above.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    private var statusLine: String {
        switch model.blocker.status {
        case .unavailable: return "Unavailable"
        case .loading: return "Loading filters…"
        case .failed(let message): return message
        case .ready(let filters):
            let formatted = NumberFormatter.localizedString(from: NSNumber(value: filters), number: .decimal)
            let version = model.blocker.engineVersion.map { " · uBO \($0)" } ?? ""
            return "\(formatted) filters\(version)"
        }
    }

    private var listsLine: String {
        guard let date = model.blocker.listsDate else { return "Filter lists not downloaded yet" }
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        return "Lists updated \(formatter.localizedString(for: date, relativeTo: Date()))"
    }
}
