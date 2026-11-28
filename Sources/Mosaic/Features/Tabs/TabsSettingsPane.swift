import MosaicCore
import SwiftUI

/// Settings › Tabs & Workspaces.
struct TabsSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        TabsSettingsForm(model: model, feature: model.tabsFeature)
    }
}

private struct TabsSettingsForm: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject var feature: TabsFeature
    @State private var pendingDeletion: Workspace?

    var body: some View {
        Form {
            startupSection
            tabsSection
            workspacesSection
        }
        .formStyle(.grouped)
        .alert(
            "Delete “\(pendingDeletion?.name ?? "")”?",
            isPresented: Binding(get: { pendingDeletion != nil }, set: { if !$0 { pendingDeletion = nil } }),
            presenting: pendingDeletion
        ) { workspace in
            Button("Delete Workspace and Close Tabs", role: .destructive) { model.deleteWorkspace(workspace.id) }
            Button("Cancel", role: .cancel) {}
        } message: { workspace in
            let count = model.tabs(in: workspace.id).filter { !$0.isStartPage }.count
            Text("Its \(count) \(count == 1 ? "tab" : "tabs") will be closed. Reopen Closed Tab (⌘⇧T) brings the workspace back with its tabs.")
        }
    }

    // MARK: Startup

    private var startupSection: some View {
        Section {
            Picker("When Mosaic starts", selection: $feature.settings.startup) {
                Text("Open previous session").tag(TabSettings.Startup.previousSession)
                Text("Start page").tag(TabSettings.Startup.startPage)
            }
            Toggle("Keep pinned tabs", isOn: $feature.settings.keepPinnedTabs)
                .disabled(feature.settings.startup == .previousSession)
                .help("With the start page, still bring back pinned tabs")
            if feature.previousSession != nil {
                LabeledContent("Last session") {
                    Button("Reopen All Tabs from Last Session") { model.restorePreviousSession() }
                }
            }
        } header: {
            Text("Startup")
        } footer: {
            Text(startupExplanation)
        }
    }

    private var startupExplanation: String {
        switch feature.settings.startup {
        case .previousSession:
            "Tabs, groups and workspaces come back as you left them. Only the tab you were on loads; the others load when you select them. After a crash your tabs are restored the same way."
        case .startPage:
            "Mosaic opens one new tab. Workspaces are kept, and History › Reopen All Tabs from Last Session brings the rest back. If Mosaic quit unexpectedly, it offers to restore your tabs."
        }
    }

    // MARK: Tabs

    private var tabsSection: some View {
        Section("Tabs") {
            Toggle("Ask before closing a pinned tab with ⌘W", isOn: $feature.settings.protectPinnedTabs)
                .help("⌘W on a pinned tab then only closes it when pressed a second time")
            Toggle("Offer to switch to a tab that is already open", isOn: $feature.settings.offerSwitchToOpenTab)
                .help("When you open an address that another tab shows, Mosaic offers to go to that tab")
            Stepper(value: $feature.settings.recentlyClosedLimit, in: TabSettings.recentlyClosedRange, step: 5) {
                LabeledContent("Remember recently closed", value: "\(feature.settings.recentlyClosedLimit)")
            }
            LabeledContent("Recently closed") {
                HStack {
                    Text("\(feature.recentlyClosed.items.count)").foregroundStyle(.secondary)
                    Button("Clear") { model.clearRecentlyClosed() }
                        .disabled(feature.recentlyClosed.items.isEmpty)
                        .accessibilityLabel("Clear recently closed tabs")
                }
            }
        }
    }

    // MARK: Workspaces

    private var workspacesSection: some View {
        Section {
            ForEach(model.workspaces) { workspace in
                WorkspaceRow(model: model, workspace: workspace) {
                    if model.workspaceDeletionNeedsConfirmation(workspace.id) {
                        pendingDeletion = workspace
                    } else {
                        model.deleteWorkspace(workspace.id)
                    }
                }
            }
            Button {
                model.createWorkspace(switchTo: false)
            } label: {
                Label("New Workspace", systemImage: "plus")
            }
        } header: {
            Text("Workspaces")
        } footer: {
            Text("Each workspace has its own tabs and groups. New tabs open in the current workspace. Switch with ⌃⌘← and ⌃⌘→, or from the Tab menu.")
        }
    }
}

private struct WorkspaceRow: View {
    @ObservedObject var model: BrowserModel
    let workspace: Workspace
    let onDelete: () -> Void
    @State private var draft = ""
    @FocusState private var editing: Bool

    var body: some View {
        let isCurrent = workspace.id == model.selectedWorkspaceID
        let count = model.tabs(in: workspace.id).count
        HStack(spacing: 10) {
            Menu {
                ForEach(TabsStyle.workspaceSymbols, id: \.self) { symbol in
                    Button {
                        model.setWorkspaceSymbol(workspace.id, symbol)
                    } label: {
                        Label(symbol, systemImage: symbol)
                    }
                }
            } label: {
                Image(systemName: workspace.symbol)
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Choose an icon")
            .accessibilityLabel("Icon for \(workspace.name)")
            TextField("Name", text: $draft)
                .textFieldStyle(.roundedBorder)
                .labelsHidden()
                .focused($editing)
                .onSubmit(commit)
                .onChange(of: editing) { _, focused in if !focused { commit() } }
                .accessibilityLabel("Name of workspace \(workspace.name)")
            Text("\(count) \(count == 1 ? "tab" : "tabs")")
                .foregroundStyle(.secondary)
                .monospacedDigit()
            if isCurrent {
                Text("Current").font(.caption).foregroundStyle(.secondary)
            } else {
                Button("Switch") { model.selectWorkspace(workspace.id) }
                    .accessibilityLabel("Switch to \(workspace.name)")
            }
            Button(role: .destructive, action: onDelete) {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .disabled(model.workspaces.count < 2)
            .help(model.workspaces.count < 2 ? "The last workspace can’t be deleted" : "Delete workspace")
            .accessibilityLabel("Delete \(workspace.name)")
        }
        .onAppear { draft = workspace.name }
        .onChange(of: workspace.name) { _, name in if !editing { draft = name } }
    }

    private func commit() {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            draft = workspace.name
        } else if trimmed != workspace.name {
            model.renameWorkspace(workspace.id, to: trimmed)
        }
    }
}
