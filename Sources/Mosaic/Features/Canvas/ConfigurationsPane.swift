import AppKit
import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// Settings › Mosaics › Configurations: named layouts (layout + appearance) to switch
/// between, rename, update, delete, export to `.json` and import (validated, with every
/// repair listed), and restoring preset defaults. Owner: **canvas**.
struct ConfigurationsPane: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.shellTheme) private var theme
    @State private var newName = ""
    @State private var renaming: String?
    @State private var renameText = ""
    @State private var confirmDelete: LayoutConfiguration?
    @State private var confirmRestoreAll = false

    private var canvas: CanvasController { model.canvas }

    var body: some View {
        let configurations = canvas.library.configurations
        let active = canvas.activeConfiguration
        Form {
            Section {
                HStack(spacing: 10) {
                    TextField("Name", text: $newName, prompt: Text(canvas.layout.name))
                        .onSubmit(save)
                    Button("Save Current Layout", action: save)
                        .buttonStyle(.borderedProminent)
                }
            } header: {
                Text("Save")
            } footer: {
                SettingsNote("Saves the window’s layout and its appearance (colours, background, New Tab and workspace backgrounds) as they are now.")
            }

            Section {
                if configurations.isEmpty {
                    Text("No saved configurations yet. Save the current layout above, or import a file.")
                        .foregroundStyle(.secondary)
                }
                ForEach(configurations) { configuration in
                    row(configuration, isActive: configuration.id == active?.id)
                }
            } header: {
                Text("Saved Configurations")
            } footer: {
                if !configurations.isEmpty {
                    SettingsNote("Switching applies the configuration’s preset with its layout; pages keep running. The preset keeps that layout until you restore its defaults.")
                }
            }

            Section {
                HStack(spacing: 10) {
                    Button { importFile() } label: { Label("Import…", systemImage: "square.and.arrow.down") }
                    Button { exportCurrent() } label: { Label("Export Current Layout…", systemImage: "square.and.arrow.up") }
                    Spacer()
                }
                if let outcome = canvas.lastImport {
                    ImportReportView(outcome: outcome)
                }
            } header: {
                Text("Import & Export")
            } footer: {
                SettingsNote("Layouts are small .json files. Imported files are checked: unknown items are dropped, values brought into range, and the address field and a way to Settings kept.")
            }

            Section {
                HStack(spacing: 10) {
                    Button("Restore \(model.mosaics.activePreset.title) Defaults") { canvas.resetToPreset() }
                        .disabled(!model.mosaics.isCustomized(model.mosaics.activePreset))
                    Button("Restore All Presets…") { confirmRestoreAll = true }
                        .disabled(!MosaicPresetID.allCases.contains(where: model.mosaics.isCustomized))
                    Spacer()
                }
            } header: {
                Text("Preset Defaults")
            } footer: {
                SettingsNote("Returns presets to their factory layouts and looks. Saved configurations are kept.")
            }
        }
        .formStyle(.grouped)
        .confirmationDialog("Delete “\(confirmDelete?.name ?? "")”?", isPresented: Binding(
            get: { confirmDelete != nil }, set: { if !$0 { confirmDelete = nil } })) {
            Button("Delete", role: .destructive) {
                if let id = confirmDelete?.id { canvas.deleteConfiguration(id) }
                confirmDelete = nil
            }
        } message: {
            Text("The window keeps its current layout.")
        }
        .confirmationDialog("Restore every preset to its defaults?", isPresented: $confirmRestoreAll) {
            Button("Restore All", role: .destructive) { canvas.restoreAllPresetDefaults() }
        } message: {
            Text("Your changes to all four presets are replaced by their factory layouts. Saved configurations are kept.")
        }
    }

    private func row(_ configuration: LayoutConfiguration, isActive: Bool) -> some View {
        HStack(spacing: 12) {
            MosaicPreview(layout: configuration.layout)
                .frame(width: 128)
                .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .strokeBorder(isActive ? theme.accent : Color.primary.opacity(0.12), lineWidth: isActive ? 2 : 1))
            VStack(alignment: .leading, spacing: 3) {
                if renaming == configuration.id {
                    TextField("Name", text: $renameText)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit { commitRename(configuration.id) }
                        .onExitCommand { renaming = nil }
                } else {
                    HStack(spacing: 6) {
                        Text(configuration.name).font(.headline)
                        if isActive {
                            Text("In Use").font(.caption.weight(.semibold)).foregroundStyle(theme.accent)
                        }
                    }
                }
                Text("\(configuration.layout.preset.title) · \(configuration.layout.tabPlacement.isVertical ? "vertical tabs" : "tabs on top") · saved \(configuration.modified.formatted(date: .abbreviated, time: .shortened))")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            if renaming == configuration.id {
                Button("Done") { commitRename(configuration.id) }
            } else if isActive {
                Button("Applied") {}.disabled(true)
            } else {
                Button("Apply") { canvas.applyConfiguration(configuration.id) }
                    .buttonStyle(.borderedProminent)
            }
            Menu {
                Button("Update with Current Layout") { canvas.updateConfiguration(configuration.id) }
                    .disabled(isActive)
                Button("Rename…") {
                    renameText = configuration.name
                    renaming = configuration.id
                }
                Button("Export…") { export(configuration) }
                Divider()
                Button("Delete…", role: .destructive) { confirmDelete = configuration }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .accessibilityLabel("\(configuration.name) options")
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(configuration.name)\(isActive ? ", in use" : "")")
    }

    private func save() {
        let name = newName.trimmingCharacters(in: .whitespaces).isEmpty ? canvas.layout.name : newName
        if canvas.saveCurrent(as: name) != nil { newName = "" }
    }

    private func commitRename(_ id: String) {
        canvas.renameConfiguration(id, to: renameText)
        renaming = nil
    }

    // MARK: Files

    private func importFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.message = "Choose a Mosaic layout (.json)"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        canvas.importFile(url)
    }

    private func exportCurrent() {
        guard let url = savePanel(name: canvas.layout.name) else { return }
        do { try canvas.export(nil, to: url) } catch { canvas.say("Couldn’t export: \(error.localizedDescription)", error: true) }
    }

    private func export(_ configuration: LayoutConfiguration) {
        guard let url = savePanel(name: configuration.name) else { return }
        do { try canvas.export(configuration.id, to: url) } catch { canvas.say("Couldn’t export: \(error.localizedDescription)", error: true) }
    }

    private func savePanel(name: String) -> URL? {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "\(name).json"
        panel.message = "Export the layout as a .json file"
        return panel.runModal() == .OK ? panel.url : nil
    }
}

/// What happened to the last imported file.
private struct ImportReportView: View {
    @EnvironmentObject private var model: BrowserModel
    let outcome: CanvasController.ImportOutcome

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Image(systemName: outcome.error == nil ? (outcome.messages.isEmpty ? "checkmark.circle.fill" : "wrench.and.screwdriver")
                                                       : "xmark.octagon.fill")
                    .foregroundStyle(outcome.error == nil ? (outcome.messages.isEmpty ? Color.green : Color.orange) : Color.red)
                    .accessibilityHidden(true)
                Text(outcome.error == nil
                     ? "Imported “\(outcome.name ?? outcome.file)” and switched to it." + (outcome.messages.isEmpty ? "" : " Repairs:")
                     : "Couldn’t import \(outcome.file).")
                    .font(.callout.weight(.medium))
                Spacer()
                Button { model.canvas.dismissImportReport() } label: { Image(systemName: "xmark") }
                    .buttonStyle(.borderless)
                    .accessibilityLabel("Dismiss")
            }
            if let error = outcome.error {
                Text(error).font(.callout).foregroundStyle(.secondary)
            }
            ForEach(outcome.messages, id: \.self) { message in
                Label(message, systemImage: "minus").labelStyle(.titleOnly)
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.leading, 26)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.04)))
    }
}

/// The Mosaics page's mode switcher (Presets · Customise Layout · Gradient Studio · Configurations).
struct CanvasModePicker: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        Picker("Mosaics", selection: Binding(get: { model.canvas.mode }, set: { model.canvas.mode = $0 })) {
            ForEach(CanvasController.Mode.allCases) { Text($0.title).tag($0) }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .fixedSize()
        .padding(.top, 12)
        .padding(.bottom, 4)
        .frame(maxWidth: .infinity)
        .accessibilityLabel("Mosaics section")
    }
}
