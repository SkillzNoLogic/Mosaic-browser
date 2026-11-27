import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// Install and manage Chrome extensions.
struct ExtensionsSheet: View {
    @EnvironmentObject private var model: BrowserModel
    @EnvironmentObject private var manager: ExtensionManager
    @Environment(\.dismiss) private var dismiss
    private var input = State(initialValue: "")

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Extensions").font(.title2.weight(.semibold))
                    Text("Chrome extensions (.crx) are converted and run on the Mozilla engine.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }

            installRow

            if let error = manager.errorMessage {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
            }

            Divider()

            if manager.extensions.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "puzzlepiece.extension").font(.system(size: 28)).foregroundStyle(.secondary)
                    Text("No extensions yet").font(.headline)
                    Text("Paste a Chrome Web Store link above, or open a listing in a tab and press Add to Mosaic.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 10) {
                        ForEach(manager.extensions) { ext in
                            ExtensionRow(ext: ext)
                        }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
        .padding(20)
        .frame(width: 640, height: 520)
    }

    private var installRow: some View {
        HStack(spacing: 8) {
            TextField("Chrome Web Store link or extension ID", text: input.projectedValue)
                .textFieldStyle(.roundedBorder)
                .onSubmit(installFromInput)
            Button("Install", action: installFromInput)
                .disabled(input.wrappedValue.trimmingCharacters(in: .whitespaces).isEmpty || manager.isBusy)
            Button("Choose .crx…", action: chooseFile)
                .disabled(manager.isBusy)
            if manager.isBusy {
                ProgressView().controlSize(.small)
            }
        }
    }

    private func installFromInput() {
        let value = input.wrappedValue
        Task {
            await manager.install(fromWebStore: value)
            if manager.errorMessage == nil { input.wrappedValue = "" }
        }
    }

    private func chooseFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = ["crx", "zip"].compactMap { UTType(filenameExtension: $0) }
        panel.allowsMultipleSelection = false
        panel.message = "Choose a Chrome extension package"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task { await manager.install(file: url) }
    }
}

private struct ExtensionRow: View {
    @EnvironmentObject private var model: BrowserModel
    @EnvironmentObject private var manager: ExtensionManager
    let ext: InstalledExtension
    private var showNotes = State(initialValue: false)

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top, spacing: 12) {
                icon
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 6) {
                        Text(ext.name).font(.headline).lineLimit(1)
                        Text(ext.version).font(.caption).foregroundStyle(.secondary)
                        Badge(text: "MV\(ext.manifestVersion)")
                        if manager.loaded.contains(ext.geckoID) {
                            Badge(text: "Running", color: .green)
                        }
                    }
                    if !ext.summary.isEmpty {
                        Text(ext.summary).font(.callout).foregroundStyle(.secondary).lineLimit(2)
                    }
                }
                Spacer()
                Toggle("Enabled", isOn: Binding(
                    get: { ext.enabled },
                    set: { value in Task { await manager.setEnabled(value, for: ext) } }
                ))
                .toggleStyle(.switch)
                .labelsHidden()
                .accessibilityLabel("Enable \(ext.name)")
            }

            HStack(spacing: 8) {
                if ext.hasPopup {
                    Button("Open Popup") { model.openExtensionPage(ext, kind: "popup") }
                }
                if ext.hasOptions {
                    Button("Options") { model.openExtensionPage(ext, kind: "options") }
                }
                if !ext.notes.isEmpty {
                    Button {
                        showNotes.wrappedValue.toggle()
                    } label: {
                        Label(compatibilityLabel, systemImage: compatibilitySymbol)
                    }
                    .foregroundStyle(compatibilityColor)
                }
                Spacer()
                Button("Remove", role: .destructive) { Task { await manager.remove(ext) } }
            }
            .controlSize(.small)

            if showNotes.wrappedValue {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(ext.notes, id: \.self) { note in
                        Label(note.message, systemImage: symbol(for: note.severity))
                            .font(.caption)
                            .foregroundStyle(color(for: note.severity))
                    }
                }
                .padding(.top, 2)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.05)))
    }

    @ViewBuilder private var icon: some View {
        if let image = manager.icon(for: ext) {
            Image(nsImage: image).resizable().interpolation(.high).frame(width: 36, height: 36)
        } else {
            Image(systemName: "puzzlepiece.extension.fill")
                .font(.system(size: 22))
                .foregroundStyle(.secondary)
                .frame(width: 36, height: 36)
        }
    }

    private var compatibilityLabel: String {
        switch ext.worstSeverity {
        case .blocker: "May not work on Gecko"
        case .warning: "Partly compatible"
        default: "Conversion notes"
        }
    }

    private var compatibilitySymbol: String { symbol(for: ext.worstSeverity ?? .info) }
    private var compatibilityColor: Color { color(for: ext.worstSeverity ?? .info) }

    private func symbol(for severity: CompatibilityNote.Severity) -> String {
        switch severity {
        case .info: "info.circle"
        case .warning: "exclamationmark.triangle"
        case .blocker: "xmark.octagon"
        }
    }

    private func color(for severity: CompatibilityNote.Severity) -> Color {
        switch severity {
        case .info: .secondary
        case .warning: .orange
        case .blocker: .red
        }
    }
}

private struct Badge: View {
    let text: String
    var color: Color = .secondary

    var body: some View {
        Text(text)
            .font(.caption2.weight(.medium))
            .padding(.horizontal, 5)
            .padding(.vertical, 1.5)
            .foregroundStyle(color)
            .overlay(Capsule().strokeBorder(color.opacity(0.5)))
    }
}
