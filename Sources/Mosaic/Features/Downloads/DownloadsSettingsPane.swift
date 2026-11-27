import AppKit
import MosaicCore
import SwiftUI

/// Settings › Downloads. Owner: **downloads**.
struct DownloadsSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        DownloadsSettingsForm(downloads: model.downloads, settings: model.downloads.settings, fdm: model.downloads.fdm)
    }
}

private struct DownloadsSettingsForm: View {
    let downloads: DownloadManager
    @ObservedObject var settings: DownloadSettings
    @ObservedObject var fdm: FreeDownloadManager

    /// Size thresholds offered for Free Download Manager, in megabytes.
    private let thresholds = [0, 10, 50, 100, 500, 1000]

    var body: some View {
        Form {
            Section("Saving") {
                LabeledContent("Download folder") {
                    HStack(spacing: 8) {
                        Label(settings.folderDisplayPath, systemImage: "folder")
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .foregroundStyle(settings.askWhereToSave ? .secondary : .primary)
                        Button("Change…", action: chooseFolder)
                    }
                }
                Toggle("Ask where to save each file", isOn: $settings.askWhereToSave)
                if settings.askWhereToSave {
                    Text("Chromium tabs save straight to the place you choose. Mozilla tabs start the download in the download folder and move the file there when it finishes.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Toggle(isOn: $settings.openSafeFiles) {
                    Text("Open safe files after downloading")
                    Text("Documents, pictures, audio and video. Never apps, installers, disk images, scripts or archives.")
                }
                Toggle(isOn: $settings.notifyWhenFinished) {
                    Text("Notify when a download finishes")
                    Text("Shown in Mosaic while you are using it, as a macOS notification otherwise. macOS asks for permission the first time you download something.")
                }
            }

            Section {
                Toggle("Sort downloads into folders by type", isOn: $settings.rules.isEnabled)
                if settings.rules.isEnabled {
                    ForEach(DownloadFileCategory.allCases) { category in
                        CategoryFolderRow(category: category, rules: $settings.rules)
                    }
                }
            } header: {
                Text("Organisation")
            } footer: {
                Text("Folders are created inside the download folder when first needed. Other kinds of files, and files you place yourself with “Ask where to save”, are left where they are saved.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Safety") {
                Label {
                    Text("Every download is marked as coming from the internet (the quarantine attribute, with its address), so macOS checks it the first time it is opened.")
                } icon: {
                    Image(systemName: "checkmark.shield")
                }
                Label {
                    Text("Mosaic warns before you open apps, installers, disk images and scripts, files whose names disguise their type (such as “invoice.pdf.app”), and files you have already downloaded. These checks happen on your Mac; nothing about your files is sent anywhere.")
                } icon: {
                    Image(systemName: "exclamationmark.triangle")
                }
            }
            .font(.callout)
            .foregroundStyle(.secondary)

            freeDownloadManagerSection
        }
        .formStyle(.grouped)
        .onAppear { fdm.refresh() }
    }

    @ViewBuilder private var freeDownloadManagerSection: some View {
        Section {
            if fdm.isInstalled {
                Picker("Use Free Download Manager", selection: $settings.fdmMode) {
                    ForEach(FDMMode.allCases) { mode in Text(mode.title).tag(mode) }
                }
                if settings.fdmMode != .off {
                    Picker("For downloads", selection: $settings.fdmMinimumMB) {
                        ForEach(thresholds, id: \.self) { megabytes in
                            Text(megabytes == 0 ? "Of any size" : "Larger than \(DownloadFormat.bytes(Int64(megabytes) * 1_000_000))")
                                .tag(megabytes)
                        }
                    }
                    Text("Downloads whose size the server does not give count as large.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Toggle("“Download Linked File with Free Download Manager” in link menus", isOn: $settings.fdmContextMenu)
                Toggle(isOn: $settings.fdmPassCookies) {
                    Text("Pass the site’s cookies")
                    Text("Lets Free Download Manager download files from sites you are signed in to. The cookies for that address are given to Free Download Manager on this Mac.")
                }
                Text(handoffExplanation)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Label {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Free Download Manager is not installed.")
                        Text("When it is (in Applications), Mosaic can hand downloads to it: always, or by asking each time. Mosaic checks again whenever this page opens.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                } icon: {
                    Image(systemName: "arrow.down.app")
                }
            }
        } header: {
            HStack {
                Text("Free Download Manager")
                if let version = fdm.version {
                    Text(version).foregroundStyle(.secondary)
                }
            }
        }
    }

    private var handoffExplanation: String {
        "Free Download Manager receives the address, the page it came from and the browser’s identity"
            + (settings.fdmPassCookies ? ", plus the site’s cookies" : "")
            + ", and asks you to confirm in its own window. It cannot receive form submissions (POST), sign-ins that use a password prompt or certificate, or files a page creates itself (blob: and data: links); those always download in Mosaic."
            + (settings.fdmPassCookies ? "" : " Without cookies, downloads from sites that need you to be signed in usually fail in Free Download Manager.")
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        panel.message = "Choose where Mosaic saves downloads"
        panel.directoryURL = settings.folder
        let apply: (NSApplication.ModalResponse) -> Void = { response in
            guard response == .OK, let url = panel.url else { return }
            settings.folderPath = url.path
        }
        if let window = NSApp.windows.first(where: { $0.frameAutosaveName == "MosaicMainWindow" && $0.isVisible }) {
            panel.beginSheetModal(for: window, completionHandler: apply)
        } else {
            apply(panel.runModal())
        }
    }
}

private struct CategoryFolderRow: View {
    let category: DownloadFileCategory
    @Binding var rules: DownloadOrganizationRules

    var body: some View {
        let enabled = Binding(
            get: { rules.folders[category] != nil },
            set: { rules.folders[category] = $0 ? category.defaultFolder : nil })
        let name = Binding(
            get: { rules.folders[category] ?? category.defaultFolder },
            set: { rules.folders[category] = $0 })
        HStack(spacing: 10) {
            Toggle(isOn: enabled) {
                Label {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(category.title)
                        Text(category.examples).font(.caption).foregroundStyle(.secondary)
                    }
                } icon: {
                    Image(systemName: category.symbol)
                }
            }
            Spacer()
            TextField("Folder", text: name)
                .textFieldStyle(.roundedBorder)
                .frame(width: 160)
                .disabled(!enabled.wrappedValue)
                .accessibilityLabel("\(category.title) folder name")
        }
    }
}
