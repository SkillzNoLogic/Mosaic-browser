import MosaicCore
import SwiftUI
import UniformTypeIdentifiers

/// Settings › Passwords & Autofill (area: passwords): saved passwords, the save and fill
/// options, the never-save list, the generator, import and export.
///
/// Showing, copying, editing or exporting a password asks for Touch ID or the Mac's login
/// password first (once per five minutes; export always asks).
struct PasswordsSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        PasswordsManagerView(passwords: model.passwords)
    }
}

struct PasswordsManagerView: View {
    let passwords: PasswordManager

    @State private var search = ""
    @State private var revealed: [CredentialID: String] = [:]
    @State private var audit: PasswordAudit?
    @State private var editing: EditorItem?
    @State private var deleting: PasswordCredential?
    @State private var importing = false
    @State private var confirmingExport = false
    @State private var exportDocument: CSVDocument?
    @State private var message: String?
    @State private var sample = PasswordGenerator.generate()

    struct EditorItem: Identifiable {
        let id = UUID()
        var credential: PasswordCredential?
        var password = ""
    }

    private var vault: PasswordVault { passwords.vault }

    var body: some View {
        let _ = passwords.revision
        Form {
            Section {
                Toggle("Offer to save passwords", isOn: binding(\.offerToSave))
                Toggle("Suggest strong passwords on sign-up forms", isOn: binding(\.suggestStrongPasswords))
                Text(verbatim: "To fill a saved password, use the key button in the address bar or press ⌘\\. Mosaic fills only when you ask, and only on the exact site the password was saved for: the same scheme, host and port. It never fills into a frame from another site, and never offers to save in private tabs.")
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } header: {
                Text("Saving and Filling")
            } footer: {
                Text("Passwords are stored in your Mac’s Keychain. Mosaic keeps only non-secret details (when a password was last used, the never-save list) in its own settings.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            savedSection
            neverSaveSection
            generatorSection
        }
        .formStyle(.grouped)
        .sheet(item: $editing) { item in
            PasswordEditor(passwords: passwords, item: item) { editing = nil }
        }
        .confirmationDialog(deleteTitle, isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
                            titleVisibility: .visible) {
            Button("Delete", role: .destructive) {
                if let credential = deleting { delete(credential) }
                deleting = nil
            }
        } message: {
            Text("This removes it from your Keychain. It can’t be undone.")
        }
        .confirmationDialog("Export passwords?", isPresented: $confirmingExport, titleVisibility: .visible) {
            Button("Export…") { Task { await export() } }
        } message: {
            Text("The file will contain all your saved passwords in plain text. Anyone who can read it can sign in as you. Keep it somewhere safe and delete it when you no longer need it.")
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.commaSeparatedText, .plainText]) { result in
            if case .success(let url) = result { importFile(url) }
        }
        .fileExporter(isPresented: Binding(get: { exportDocument != nil }, set: { if !$0 { exportDocument = nil } }),
                      document: exportDocument, contentType: .commaSeparatedText,
                      defaultFilename: "Mosaic Passwords.csv") { result in
            exportDocument = nil
            if case .failure(let error) = result { message = "Couldn’t export: \(error.localizedDescription)" }
            else { message = "Passwords exported. Delete the file once you have imported it elsewhere." }
        }
        .alert(message ?? "", isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
            Button("OK") { message = nil }
        }
        .onAppear { passwords.changed() }
        .onDisappear {
            revealed = [:]
            passwords.authenticator.lock()
        }
    }

    // MARK: Saved passwords

    private var savedSection: some View {
        let list = vault.search(search)
        return Section {
            HStack(spacing: 8) {
                TextField("Search sites and usernames", text: $search)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Search passwords")
                Button {
                    editing = EditorItem(credential: nil, password: PasswordGenerator.generate(vault.preferences.generator))
                } label: {
                    Label("Add", systemImage: "plus")
                }
                .help("Add a password")
                Menu {
                    Button("Import from CSV…") { importing = true }
                    Button("Export to CSV…") { confirmingExport = true }
                        .disabled(vault.credentials.isEmpty)
                    Divider()
                    Button("Check for Weak and Reused Passwords") { Task { await runAudit() } }
                        .disabled(vault.credentials.isEmpty)
                } label: {
                    Label("More", systemImage: "ellipsis.circle")
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .accessibilityLabel("More password actions")
            }
            if let error = vault.lastError {
                Label(error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            }
            if let audit {
                auditSummary(audit)
            }
            if list.isEmpty {
                Text(search.isEmpty ? "No saved passwords yet. Mosaic offers to save one when you sign in to a site, or add one with Add."
                                    : "No passwords match “\(search)”.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.vertical, 6)
            } else {
                ForEach(list) { credential in
                    row(credential)
                }
            }
        } header: {
            Text("Saved Passwords (\(vault.credentials.count))")
        }
    }

    private func auditSummary(_ audit: PasswordAudit) -> some View {
        let weak = audit.weak.count, reused = audit.reused.count
        return HStack(spacing: 6) {
            Image(systemName: weak + reused == 0 ? "checkmark.shield" : "exclamationmark.shield")
                .foregroundStyle(weak + reused == 0 ? .green : .orange)
            Text(weak + reused == 0 ? "No weak or reused passwords."
                 : "\(weak) weak, \(reused) reused. Change them on their sites, then update them here.")
            Spacer()
            Text("Checked on this Mac only").font(.caption).foregroundStyle(.secondary)
        }
        .font(.callout)
    }

    private func row(_ credential: PasswordCredential) -> some View {
        let id = credential.id
        let shown = revealed[id]
        return HStack(spacing: 10) {
            Image(systemName: credential.origin.scheme == "https" ? "globe" : "exclamationmark.triangle")
                .foregroundStyle(credential.origin.scheme == "https" ? Color.secondary : Color.orange)
                .frame(width: 18)
                .help(credential.origin.scheme == "https" ? credential.origin.string : "Saved for an unencrypted (http) site")
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(credential.origin.displayName).fontWeight(.medium).lineLimit(1).truncationMode(.middle)
                Text(credential.username.isEmpty ? "No username" : credential.username)
                    .font(.callout).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 8)
            if let audit {
                if audit.weak.contains(id) { badge("Weak", .orange) }
                if audit.reused.contains(id) { badge("Reused", .red) }
            }
            Text(shown ?? "••••••••")
                .font(.system(.callout, design: .monospaced))
                .lineLimit(1).truncationMode(.middle)
                .frame(maxWidth: 180, alignment: .trailing)
                .textSelection(.enabled)
                .accessibilityLabel(shown == nil ? "Password hidden" : "Password")
            Button {
                if shown != nil { revealed[id] = nil } else { Task { await reveal(id) } }
            } label: {
                Image(systemName: shown == nil ? "eye" : "eye.slash")
            }
            .buttonStyle(.borderless)
            .help(shown == nil ? "Show password" : "Hide password")
            .accessibilityLabel(shown == nil ? "Show password for \(credential.origin.displayName)" : "Hide password")
            Menu {
                Button("Copy Username") { PasswordClipboard.copy(credential.username, secret: false) }
                    .disabled(credential.username.isEmpty)
                Button("Copy Password") { Task { await copyPassword(id) } }
                Divider()
                Button("Edit…") { Task { await edit(credential) } }
                Button("Delete…", role: .destructive) { deleting = credential }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .accessibilityLabel("Actions for \(credential.origin.displayName)")
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(credential.origin.displayName), \(credential.username.isEmpty ? "no username" : credential.username)")
    }

    private func badge(_ text: String, _ color: Color) -> some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 6).padding(.vertical, 2)
            .background(Capsule().fill(color.opacity(0.18)))
            .foregroundStyle(color)
    }

    // MARK: Never save

    private var neverSaveSection: some View {
        Section {
            if vault.preferences.neverSave.isEmpty {
                Text("When you choose “Never for This Site” in the save prompt, the site is listed here.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(vault.preferences.neverSave, id: \.self) { origin in
                    HStack {
                        Text(origin.displayName)
                        Spacer()
                        Button("Remove") {
                            vault.setNeverSave(false, for: origin)
                            passwords.changed()
                        }
                        .accessibilityLabel("Offer to save passwords on \(origin.displayName) again")
                    }
                }
            }
        } header: {
            Text("Never Saved For")
        }
    }

    // MARK: Generator

    private var generatorSection: some View {
        let options = vault.preferences.generator
        return Section {
            HStack {
                Text(sample)
                    .font(.system(.body, design: .monospaced))
                    .lineLimit(1).truncationMode(.middle)
                    .textSelection(.enabled)
                    .accessibilityLabel("Generated password")
                Spacer()
                Button {
                    sample = PasswordGenerator.generate(vault.preferences.generator)
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .help("Generate another")
                .accessibilityLabel("Generate another password")
                Button {
                    PasswordClipboard.copy(sample, secret: true)
                    passwords.showNotice("Password copied")
                } label: {
                    Image(systemName: "doc.on.doc")
                }
                .buttonStyle(.borderless)
                .help("Copy")
                .accessibilityLabel("Copy generated password")
            }
            LabeledContent("Length") {
                HStack {
                    Slider(value: Binding(get: { Double(options.length) },
                                          set: { value in updateGenerator { $0.length = Int(value.rounded()) } }),
                           in: 8...64)
                        .frame(maxWidth: 220)
                        .accessibilityLabel("Password length")
                        .accessibilityValue("\(options.effectiveLength) characters")
                    Text("\(options.effectiveLength)").monospacedDigit().frame(width: 28, alignment: .trailing)
                }
            }
            Toggle("Lowercase letters (a–z)", isOn: generatorBinding(\.lowercase))
            Toggle("Capital letters (A–Z)", isOn: generatorBinding(\.uppercase))
            Toggle("Digits (0–9)", isOn: generatorBinding(\.digits))
            Toggle("Symbols (-_.!@#$%&*+=?)", isOn: generatorBinding(\.symbols))
            Toggle("Avoid look-alike characters (l, 1, I, O, 0)", isOn: generatorBinding(\.avoidAmbiguous))
            HStack {
                Spacer()
                Button("Restore Defaults") { updateGenerator { $0 = .default } }
                    .disabled(options == .default)
            }
        } header: {
            Text("Password Generator")
        } footer: {
            Text("About \(Int(options.entropyBits)) bits of randomness. Used for suggestions on sign-up forms and for new passwords here.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func updateGenerator(_ change: (inout PasswordGeneratorOptions) -> Void) {
        vault.update { change(&$0.generator) }
        sample = PasswordGenerator.generate(vault.preferences.generator)
        passwords.changed()
    }

    private func generatorBinding(_ key: WritableKeyPath<PasswordGeneratorOptions, Bool>) -> Binding<Bool> {
        Binding(get: { vault.preferences.generator[keyPath: key] },
                set: { value in updateGenerator { $0[keyPath: key] = value } })
    }

    private func binding(_ key: WritableKeyPath<PasswordPreferences, Bool>) -> Binding<Bool> {
        Binding(get: { vault.preferences[keyPath: key] },
                set: { value in
                    vault.update { $0[keyPath: key] = value }
                    passwords.changed()
                })
    }

    // MARK: Actions

    private var deleteTitle: String {
        guard let deleting else { return "" }
        return "Delete the password for \(deleting.username.isEmpty ? "this login" : deleting.username) on \(deleting.origin.displayName)?"
    }

    private func reveal(_ id: CredentialID) async {
        guard await passwords.authenticator.authenticate(reason: "show a saved password") else { return }
        do {
            revealed[id] = try vault.password(for: id)
        } catch {
            message = error.localizedDescription
        }
    }

    private func copyPassword(_ id: CredentialID) async {
        guard await passwords.authenticator.authenticate(reason: "copy a saved password") else { return }
        do {
            PasswordClipboard.copy(try vault.password(for: id), secret: true)
            passwords.showNotice("Password copied. It is cleared from the clipboard in a minute.")
        } catch {
            message = error.localizedDescription
        }
    }

    private func edit(_ credential: PasswordCredential) async {
        guard await passwords.authenticator.authenticate(reason: "edit a saved password") else { return }
        do {
            editing = EditorItem(credential: credential, password: try vault.password(for: credential.id))
        } catch {
            message = error.localizedDescription
        }
    }

    private func delete(_ credential: PasswordCredential) {
        do {
            try vault.delete(credential.id)
            revealed[credential.id] = nil
            passwords.changed()
        } catch {
            message = "Couldn’t delete: \(error.localizedDescription)"
        }
    }

    private func runAudit() async {
        guard await passwords.authenticator.authenticate(reason: "check your saved passwords") else { return }
        audit = vault.audit()
    }

    private func importFile(_ url: URL) {
        let access = url.startAccessingSecurityScopedResource()
        defer { if access { url.stopAccessingSecurityScopedResource() } }
        guard let data = try? Data(contentsOf: url),
              let text = String(data: data, encoding: .utf8) ?? String(data: data, encoding: .isoLatin1)
        else {
            message = "Couldn’t read that file."
            return
        }
        do {
            let summary = vault.importEntries(try PasswordCSV.parseExport(text))
            passwords.changed()
            message = PasswordsManagerView.describe(summary)
        } catch {
            message = error.localizedDescription
        }
    }

    static func describe(_ summary: PasswordVault.ImportSummary) -> String {
        var parts = ["\(summary.added) added"]
        if summary.updated > 0 { parts.append("\(summary.updated) updated") }
        if summary.unchanged > 0 { parts.append("\(summary.unchanged) already saved") }
        if summary.failed > 0 { parts.append("\(summary.failed) could not be saved") }
        if !summary.skipped.isEmpty { parts.append("\(summary.skipped.count) skipped (no password, or not a web address)") }
        return "Import finished: " + parts.joined(separator: ", ")
            + ".\n\nThe CSV file holds your passwords in plain text: delete it now."
    }

    private func export() async {
        guard await passwords.authenticator.authenticate(reason: "export your saved passwords", fresh: true) else { return }
        do {
            exportDocument = CSVDocument(text: PasswordCSV.export(try vault.exportEntries()))
        } catch {
            message = "Couldn’t export: \(error.localizedDescription)"
        }
    }
}

/// Add or edit one password.
private struct PasswordEditor: View {
    let passwords: PasswordManager
    let item: PasswordsManagerView.EditorItem
    let done: () -> Void

    @State private var website = ""
    @State private var username = ""
    @State private var password = ""
    @State private var showPassword = true
    @State private var error: String?

    var body: some View {
        let strength = PasswordStrength.evaluate(password)
        VStack(alignment: .leading, spacing: 14) {
            Text(item.credential == nil ? "Add Password" : "Edit Password").font(.title3.bold())
            Form {
                TextField("Website", text: $website, prompt: Text("example.com"))
                    .disabled(item.credential != nil)
                    .help(item.credential != nil ? "To use the password on another site, add it there." : "")
                TextField("Username", text: $username, prompt: Text("Optional"))
                HStack {
                    Group {
                        if showPassword {
                            TextField("Password", text: $password)
                        } else {
                            SecureField("Password", text: $password)
                        }
                    }
                    .font(.system(.body, design: .monospaced))
                    Button {
                        showPassword.toggle()
                    } label: {
                        Image(systemName: showPassword ? "eye.slash" : "eye")
                    }
                    .buttonStyle(.borderless)
                    .accessibilityLabel(showPassword ? "Hide password" : "Show password")
                    Button("Generate") {
                        password = PasswordGenerator.generate(passwords.vault.preferences.generator)
                    }
                }
                if !password.isEmpty {
                    LabeledContent("Strength") {
                        Text(strength.title)
                            .foregroundStyle(strength == .weak ? .orange : strength == .fair ? .yellow : .green)
                    }
                }
            }
            .formStyle(.grouped)
            if let error {
                Label(error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.red)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { done() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(password.isEmpty || (item.credential == nil && SiteOrigin(userInput: website) == nil))
            }
        }
        .padding(20)
        .frame(width: 460)
        .onAppear {
            website = item.credential?.origin.string ?? ""
            username = item.credential?.username ?? ""
            password = item.password
        }
    }

    private func save() {
        let vault = passwords.vault
        let name = username.trimmingCharacters(in: .whitespacesAndNewlines)
        do {
            if let credential = item.credential {
                try vault.edit(credential.id, username: name, password: password)
            } else {
                guard let origin = SiteOrigin(userInput: website) else {
                    error = "Enter a website address, like example.com."
                    return
                }
                let id = CredentialID(origin: origin, username: name)
                if vault.credentials.contains(where: { $0.id == id }) {
                    error = PasswordStoreError.duplicate.localizedDescription
                    return
                }
                try vault.save(id, password: password)
            }
            passwords.changed()
            done()
        } catch {
            self.error = error.localizedDescription
        }
    }
}

/// A CSV file for `fileExporter`.
struct CSVDocument: FileDocument {
    static var readableContentTypes: [UTType] { [.commaSeparatedText] }
    var text: String

    init(text: String) {
        self.text = text
    }

    init(configuration: ReadConfiguration) throws {
        text = String(data: configuration.file.regularFileContents ?? Data(), encoding: .utf8) ?? ""
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        let wrapper = FileWrapper(regularFileWithContents: Data(text.utf8))
        // Readable by the owner only.
        wrapper.fileAttributes[FileAttributeKey.posixPermissions.rawValue] = 0o600
        return wrapper
    }
}
