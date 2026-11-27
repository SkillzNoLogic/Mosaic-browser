import MosaicCore
import SwiftUI

/// Mount point: toolbar button showing download progress, opening the downloads popover.
/// Owner: **downloads**. The shell places this view. It shows only once there is
/// something in the download list (as Safari does), so it never sits in the toolbar unused.
struct DownloadsToolbarButton: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        DownloadsToolbarButtonContent(downloads: model.downloads)
    }
}

private struct DownloadsToolbarButtonContent: View {
    @ObservedObject var downloads: DownloadManager
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        if !downloads.items.isEmpty {
            Button {
                downloads.isPopoverShown.toggle()
            } label: {
                ZStack {
                    if downloads.activeCount > 0 {
                        DownloadProgressRing(progress: downloads.overallProgress, animated: !reduceMotion)
                            .frame(width: 22, height: 22)
                    }
                    Image(systemName: downloads.activeCount > 0 ? "arrow.down" : "arrow.down.circle")
                        .font(.system(size: downloads.activeCount > 0 ? 11 : 15, weight: .semibold))
                }
                .frame(width: 28, height: 28)
                .overlay(alignment: .topTrailing) {
                    if downloads.hasUnseenCompletion, downloads.activeCount == 0 {
                        Circle()
                            .fill(Color.accentColor)
                            .frame(width: 7, height: 7)
                            .offset(x: -2, y: 3)
                            .accessibilityHidden(true)
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(helpText)
            .accessibilityLabel("Downloads")
            .accessibilityValue(helpText)
            .popover(isPresented: $downloads.isPopoverShown, arrowEdge: .bottom) {
                DownloadsPopover(downloads: downloads)
            }
        }
    }

    private var helpText: String {
        let active = downloads.activeCount
        if active == 0 {
            return downloads.hasUnseenCompletion ? "Downloads (finished)" : "Downloads (⌥⌘L for all)"
        }
        let percent = downloads.overallProgress.map { ", \(Int($0 * 100))%" } ?? ""
        return active == 1 ? "1 download in progress\(percent)" : "\(active) downloads in progress\(percent)"
    }
}

/// A ring filling with overall progress; a turning arc when the size is unknown (a still
/// one with reduced motion).
struct DownloadProgressRing: View {
    let progress: Double?
    let animated: Bool
    @State private var spinning = false

    var body: some View {
        ZStack {
            Circle().stroke(Color.primary.opacity(0.15), lineWidth: 2.2)
            if let progress {
                Circle()
                    .trim(from: 0, to: max(0.02, progress))
                    .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 2.2, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(animated ? .easeOut(duration: 0.3) : nil, value: progress)
            } else {
                Circle()
                    .trim(from: 0, to: 0.28)
                    .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 2.2, lineCap: .round))
                    .rotationEffect(.degrees(spinning ? 270 : -90))
                    .onAppear {
                        guard animated else { return }
                        withAnimation(.linear(duration: 1.1).repeatForever(autoreverses: false)) { spinning = true }
                    }
            }
        }
        .accessibilityHidden(true)
    }
}

/// The compact list of recent downloads under the toolbar button.
struct DownloadsPopover: View {
    @ObservedObject var downloads: DownloadManager

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Downloads").font(.headline)
                Spacer()
                Button("Show All") { downloads.openDownloadsPage() }
                    .buttonStyle(.link)
                    .help("Open the Downloads page (⌥⌘L)")
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            Divider()
            if downloads.items.isEmpty {
                Text("No downloads")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(24)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(downloads.items.prefix(12)) { record in
                            DownloadRowView(downloads: downloads, record: record, compact: true)
                                .padding(.horizontal, 14)
                                .onTapGesture(count: 2) { downloads.open(record.id) }
                                .contextMenu { DownloadContextMenu(downloads: downloads, record: record) }
                            Divider().padding(.leading, 52)
                        }
                    }
                }
                .frame(maxHeight: 380)
            }
            Divider()
            HStack(spacing: 8) {
                Button("Clear") { downloads.clearFinished() }
                    .disabled(!downloads.items.contains { !$0.state.isActive })
                    .help("Remove finished downloads from the list (files stay)")
                Spacer()
                Button {
                    downloads.openFolder()
                } label: {
                    Label(downloads.settings.folderDisplayPath, systemImage: "folder")
                        .lineLimit(1)
                        .truncationMode(.head)
                }
                .buttonStyle(.borderless)
                .help("Open the download folder")
            }
            .controlSize(.small)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
        }
        .frame(width: 360)
    }
}
