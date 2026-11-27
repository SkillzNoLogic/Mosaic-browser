import AppKit
import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// One download in the popover or on the Downloads page.
struct DownloadRowView: View {
    @ObservedObject var downloads: DownloadManager
    let record: DownloadRecord
    /// The popover's denser layout.
    var compact = false

    var body: some View {
        let exists = record.state == .completed ? downloads.fileExists(record) : true
        HStack(alignment: .top, spacing: 10) {
            DownloadFileIcon(record: record, size: compact ? 28 : 36)
            VStack(alignment: .leading, spacing: 3) {
                Text(record.fileName)
                    .font(compact ? .callout.weight(.medium) : .body.weight(.medium))
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(record.fileName)
                if record.state.isActive {
                    progress
                }
                Text(statusLine(exists: exists))
                    .font(.caption)
                    .foregroundStyle(record.state == .failed ? Color.red : Color.secondary)
                    .lineLimit(compact ? 1 : 2)
                if !compact {
                    Text(detailLine)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                if record.state == .completed, exists, record.needsSafetyConfirmation {
                    DownloadWarningBox(downloads: downloads, record: record, compact: compact)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            DownloadActionButtons(downloads: downloads, record: record, fileExists: exists)
        }
        .padding(.vertical, compact ? 6 : 4)
        .contentShape(Rectangle())
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(record.fileName), \(statusLine(exists: exists))")
    }

    @ViewBuilder private var progress: some View {
        if let fraction = record.fractionCompleted {
            ProgressView(value: fraction)
                .progressViewStyle(.linear)
                .controlSize(.small)
                .tint(record.state == .paused ? .secondary : .accentColor)
                .accessibilityLabel("Progress")
                .accessibilityValue("\(Int(fraction * 100)) percent")
        } else {
            ProgressView()
                .progressViewStyle(.linear)
                .controlSize(.small)
                .accessibilityLabel("Downloading, size unknown")
        }
    }

    private func statusLine(exists: Bool) -> String {
        if record.state == .completed, !exists { return "Moved or deleted" }
        return DownloadFormat.status(record)
    }

    private var detailLine: String {
        var parts: [String] = []
        if let host = record.host { parts.append(host) }
        parts.append(record.engine.displayName)
        parts.append(record.startedAt.formatted(date: .omitted, time: .shortened))
        if let path = record.destinationPath, record.state == .completed {
            parts.append(((path as NSString).deletingLastPathComponent as NSString).abbreviatingWithTildeInPath)
        }
        return parts.joined(separator: " · ")
    }
}

/// The file's Finder icon, or its type's icon until the file exists.
struct DownloadFileIcon: View {
    let record: DownloadRecord
    let size: CGFloat

    var body: some View {
        Image(nsImage: icon)
            .resizable()
            .interpolation(.high)
            .frame(width: size, height: size)
            .overlay(alignment: .bottomTrailing) {
                if record.state == .handedOff {
                    Image(systemName: "arrow.up.forward.app.fill")
                        .font(.system(size: size * 0.36))
                        .foregroundStyle(.white, Color.accentColor)
                        .accessibilityHidden(true)
                }
            }
            .accessibilityHidden(true)
    }

    private var icon: NSImage {
        if record.state == .completed, let path = record.destinationPath, FileManager.default.fileExists(atPath: path) {
            return NSWorkspace.shared.icon(forFile: path)
        }
        let ext = DownloadFileName.pathExtension(record.fileName)
        let type = ext.isEmpty ? UTType.data : (UTType(filenameExtension: ext) ?? .data)
        return NSWorkspace.shared.icon(for: type)
    }
}

/// The buttons beside a download: pause/resume/cancel while running, retry when stopped,
/// show in Finder when done.
struct DownloadActionButtons: View {
    @ObservedObject var downloads: DownloadManager
    let record: DownloadRecord
    let fileExists: Bool

    var body: some View {
        HStack(spacing: 2) {
            switch record.state {
            case .inProgress:
                icon("pause.circle", "Pause") { downloads.pause(record.id) }
                icon("xmark.circle", "Cancel") { downloads.cancel(record.id) }
            case .paused:
                icon("play.circle", "Resume") { downloads.resume(record.id) }
                icon("xmark.circle", "Cancel") { downloads.cancel(record.id) }
            case .failed, .cancelled:
                icon("arrow.clockwise.circle", record.engine == .fdm ? "Download in Mosaic" : "Retry") {
                    downloads.retry(record.id)
                }
            case .completed:
                if fileExists {
                    icon("magnifyingglass.circle", "Show in Finder") { downloads.revealInFinder(record.id) }
                } else {
                    icon("arrow.clockwise.circle", "Download Again") { downloads.retry(record.id) }
                }
            case .handedOff:
                EmptyView()
            }
        }
    }

    private func icon(_ symbol: String, _ label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 17))
                .frame(width: 28, height: 28)
                .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .help(label)
        .accessibilityLabel(label)
    }
}

/// "This file is an application…" with Keep / Move to Trash.
struct DownloadWarningBox: View {
    @ObservedObject var downloads: DownloadManager
    let record: DownloadRecord
    var compact = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label {
                Text(record.assessment.summary ?? "This file may be dangerous.")
                    .fixedSize(horizontal: false, vertical: true)
            } icon: {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            }
            .font(.caption)
            HStack(spacing: 8) {
                Button("Keep") { downloads.keep(record.id) }
                    .help("Keep the file; you will not be warned about it again")
                Button("Move to Trash", role: .destructive) { downloads.trash(record.id) }
            }
            .controlSize(.small)
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.orange.opacity(0.12)))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Warning")
    }
}

/// The menu shared by the popover rows and the Downloads page.
struct DownloadContextMenu: View {
    @ObservedObject var downloads: DownloadManager
    let record: DownloadRecord

    var body: some View {
        let exists = downloads.fileExists(record)
        if record.state == .completed, exists {
            Button("Open") { downloads.open(record.id) }
            Button("Show in Finder") { downloads.revealInFinder(record.id) }
        }
        switch record.state {
        case .inProgress:
            Button("Pause") { downloads.pause(record.id) }
            Button("Cancel") { downloads.cancel(record.id) }
        case .paused:
            Button("Resume") { downloads.resume(record.id) }
            Button("Cancel") { downloads.cancel(record.id) }
        case .failed, .cancelled:
            Button(record.engine == .fdm ? "Download in Mosaic" : "Retry") { downloads.retry(record.id) }
        default:
            EmptyView()
        }
        Divider()
        Button("Copy Download Link") { downloads.copyLink(record.id) }
        if downloads.fdm.isInstalled, FDMPolicy.supportedSchemes.contains(URL(string: record.url)?.scheme?.lowercased() ?? "") {
            Button("Download with Free Download Manager") { downloads.sendToFDM(record.id) }
        }
        Divider()
        Button("Remove from List") { downloads.remove(record.id) }
        if record.state == .completed, exists {
            Button("Move to Trash", role: .destructive) { downloads.trash(record.id) }
        }
    }
}
