import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// Settings › Extensions: install Chrome extensions (Web Store link or ID, or a `.crx`
/// file), and manage them: enable, popup, options, permissions, compatibility, remove.
struct ExtensionsSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        ExtensionsForm(manager: model.extensions)
    }
}

private struct ExtensionsForm: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var manager: ExtensionManager
    @State private var input = ""

    var body: some View {
        Form {
            Section {
                SettingsNote("""
                    Mosaic converts Chrome extensions and runs them in Mozilla tabs. Chromium tabs can’t run \
                    extensions: the embedded Chromium has no extension support. Most extensions work; anything \
                    an extension relies on that Gecko lacks is listed next to it.
                    """)
                if model.geckoRuntimeMissing {
                    HStack {
                        Label("Extensions need the Mozilla engine, which is not installed.", systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                        Spacer()
                        Button("Install…") { model.openSettings(.engine) }
                    }
                    .font(.callout)
                }
            }

            Section("Add an Extension") {
                HStack(spacing: 8) {
                    TextField("Web Store link or ID", text: $input,
                              prompt: Text("https://chromewebstore.google.com/detail/…"))
                        .onSubmit(installFromInput)
                        .accessibilityLabel("Chrome Web Store link or extension ID")
                    Button("Install", action: installFromInput)
                        .disabled(input.trimmingCharacters(in: .whitespaces).isEmpty || manager.isBusy)
                    Button("Choose File…", action: chooseFile)
                        .disabled(manager.isBusy)
                        .help("Install a .crx or zipped extension from disk")
                    if manager.isBusy {
                        ProgressView().controlSize(.small).accessibilityLabel("Installing")
                    }
                }
                if let error = manager.errorMessage {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout)
                        .foregroundStyle(.red)
                        .textSelection(.enabled)
                }
                SettingsNote("Or open an extension’s page on the Chrome Web Store in any tab and press Add to Mosaic in the toolbar.")
            }

            Section(manager.extensions.isEmpty ? "Installed" : "Installed (\(manager.extensions.count))") {
                if manager.extensions.isEmpty {
                    HStack(spacing: 10) {
                        Image(systemName: "puzzlepiece.extension").font(.title2).foregroundStyle(.secondary)
                            .accessibilityHidden(true)
                        Text("No extensions installed yet.").foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 6)
                } else {
                    ForEach(manager.extensions) { ext in
                        ExtensionRow(ext: ext, manager: manager)
                    }
                }
            }

            Section {
                SettingsNote("""
                    Popups open in a Mozilla tab, not a bubble under the toolbar. Extensions don’t update \
                    themselves: install again to update (settings are kept).
                    """)
            }
        }
        .formStyle(.grouped)
    }

    private func installFromInput() {
        let value = input
        Task {
            await manager.install(fromWebStore: value)
            if manager.errorMessage == nil { input = "" }
        }
    }

    private func chooseFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = ["crx", "zip"].compactMap { UTType(filenameExtension: $0) }
        panel.allowsMultipleSelection = false
        panel.message = "Choose a Chrome extension package (.crx or .zip)"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task { await manager.install(file: url) }
    }
}

private struct ExtensionRow: View {
    @EnvironmentObject private var model: BrowserModel
    let ext: InstalledExtension
    @ObservedObject var manager: ExtensionManager
    @State private var showDetails = false
    @State private var confirmRemove = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top, spacing: 12) {
                icon
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 6) {
                        Text(ext.name).font(.headline).lineLimit(1)
                        Text(ext.version).font(.caption).foregroundStyle(.secondary)
                        StatusBadge(text: "MV\(ext.manifestVersion)")
                            .help("Manifest version \(ext.manifestVersion)")
                        state
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
                    Button("Open Popup") { open("popup") }.disabled(!ext.enabled)
                }
                if ext.hasOptions {
                    Button("Options") { open("options") }.disabled(!ext.enabled)
                }
                Button {
                    showDetails.toggle()
                } label: {
                    Label(detailsLabel, systemImage: compatibilitySymbol)
                        .font(.system(size: NSFont.smallSystemFontSize))
                }
                .foregroundStyle(compatibilityColor)
                .accessibilityHint(showDetails ? "Hides permissions and compatibility" : "Shows permissions and compatibility")
                Spacer()
                Button("Remove…", role: .destructive) { confirmRemove = true }
            }
            .controlSize(.small)

            if showDetails { details }
        }
        .padding(.vertical, 4)
        .confirmationDialog("Remove \(ext.name)?", isPresented: $confirmRemove) {
            Button("Remove", role: .destructive) { Task { await manager.remove(ext) } }
        } message: {
            Text("Its settings and data are deleted.")
        }
    }

    private func open(_ kind: String) {
        model.closeSettings()
        model.openExtensionPage(ext, kind: kind)
    }

    @ViewBuilder private var state: some View {
        if !ext.enabled {
            StatusBadge(text: "Off")
        } else if manager.loaded.contains(ext.geckoID) {
            StatusBadge(text: "Running", tone: .positive)
        } else if model.gecko.isRunning {
            StatusBadge(text: "Not loaded", tone: .negative)
        } else {
            StatusBadge(text: "Starts with Mozilla engine")
                .help("Loaded when the Mozilla engine starts")
        }
    }

    @ViewBuilder private var details: some View {
        let permissions = manager.permissions(for: ext)
        VStack(alignment: .leading, spacing: 6) {
            Text("Permissions").font(.subheadline.weight(.semibold))
            if permissions.readsAllSites {
                CapabilityRow(kind: .info, text: "Can read and change all websites")
            } else if !permissions.sites.isEmpty {
                CapabilityRow(kind: .info, text: "Can read and change: " + permissions.sites.prefix(6).joined(separator: ", ")
                    + (permissions.sites.count > 6 ? " and \(permissions.sites.count - 6) more" : ""))
            }
            if !permissions.apis.isEmpty {
                CapabilityRow(kind: .info, text: "Uses: " + permissions.apis.joined(separator: ", "))
            }
            if permissions.sites.isEmpty && permissions.apis.isEmpty {
                CapabilityRow(kind: .info, text: "Asks for no special permissions")
            }
            Text("Compatibility with Gecko").font(.subheadline.weight(.semibold)).padding(.top, 4)
            if ext.notes.isEmpty {
                CapabilityRow(kind: .yes, text: "Converted without changes that affect it")
            }
            ForEach(ext.notes, id: \.self) { note in
                Label(note.message, systemImage: symbol(for: note.severity))
                    .font(.callout)
                    .foregroundStyle(color(for: note.severity))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.04)))
    }

    @ViewBuilder private var icon: some View {
        if let image = manager.icon(for: ext) {
            Image(nsImage: image).resizable().interpolation(.high).frame(width: 36, height: 36)
                .accessibilityHidden(true)
        } else {
            Image(systemName: "puzzlepiece.extension.fill")
                .font(.system(size: 22))
                .foregroundStyle(.secondary)
                .frame(width: 36, height: 36)
                .accessibilityHidden(true)
        }
    }

    private var detailsLabel: String {
        switch ext.worstSeverity {
        case .blocker: "May not work on Gecko"
        case .warning: "Partly compatible"
        default: "Permissions & Compatibility"
        }
    }

    private var compatibilitySymbol: String { symbol(for: ext.worstSeverity ?? .info) }
    private var compatibilityColor: Color {
        ext.worstSeverity.map(color(for:)) ?? .primary
    }

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

/// Toolbar buttons for enabled extensions, ready for the shell to place in each Mosaic's
/// toolbar. Clicking one opens the extension's popup (in a Mozilla tab).
struct ExtensionToolbarButtons: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        HStack(spacing: 2) {
            ForEach(model.extensionToolbarItems) { item in
                Button(action: item.perform) {
                    Group {
                        if let icon = item.icon {
                            Image(nsImage: icon).resizable().interpolation(.high).frame(width: 16, height: 16)
                        } else {
                            Image(systemName: "puzzlepiece.extension.fill").font(.system(size: 13))
                        }
                    }
                    .frame(width: 28, height: 28)
                    .contentShape(Rectangle())
                    .opacity(item.isRunning || !model.gecko.isRunning ? 1 : 0.5)
                }
                .buttonStyle(.plain)
                .help(item.help)
                .accessibilityLabel(item.name)
            }
        }
    }
}
