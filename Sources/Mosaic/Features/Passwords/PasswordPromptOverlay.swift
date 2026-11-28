import MosaicCore
import SwiftUI

/// Mount point (area: passwords): the compact password prompt for the selected tab — Save /
/// Update after a sign-in, a suggested strong password on a sign-up form, or the choice of
/// login when a site has several. **The shell places it over the content area, aligned to
/// the top trailing corner** (under the address bar's key button), in every Mosaic. It sizes
/// itself (about 340 pt wide) and draws nothing when there is nothing to ask.
///
/// It draws over Gecko pages too (Mosaic's window is in front), so it works in both engines.
/// It never takes keyboard shortcuts, so typing in the page is never intercepted.
struct PasswordPromptOverlay: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let tab = model.selectedTab
        let prompt = model.passwords.prompt(for: tab)
        ZStack {
            if let tab, let prompt {
                PasswordPromptCard(tab: tab, prompt: prompt, passwords: model.passwords)
                    .id(prompt.key)
                    .padding(12)
                    .transition(reduceMotion ? .opacity : .move(edge: .top).combined(with: .opacity))
            }
        }
        .animation(reduceMotion ? nil : .snappy(duration: 0.2), value: prompt)
    }
}

struct PasswordPromptCard: View {
    let tab: BrowserTab
    let prompt: PasswordManager.Prompt
    let passwords: PasswordManager
    @State private var reveal = false
    @State private var username = ""
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            switch prompt {
            case .save(let submission, let offer): save(submission, offer)
            case .generate(_, let password): generate(password)
            case .choose(let origin, let credentials): choose(origin, credentials)
            }
        }
        .padding(14)
        .frame(width: 340, alignment: .leading)
        .background {
            let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
            if reduceTransparency {
                shape.fill(Color(nsColor: .windowBackgroundColor))
            } else {
                shape.fill(.regularMaterial)
            }
        }
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.quaternary))
        .shadow(color: .black.opacity(0.18), radius: 14, y: 6)
        .accessibilityElement(children: .contain)
    }

    private func header(_ title: String, symbol: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: symbol).foregroundStyle(.tint).accessibilityHidden(true)
            Text(title).font(.headline).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button {
                passwords.dismissPrompt(for: tab)
            } label: {
                Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).frame(width: 20, height: 20)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .help("Close")
            .accessibilityLabel("Close")
        }
    }

    // MARK: Save / update

    @ViewBuilder
    private func save(_ submission: PasswordSubmission, _ offer: SaveOffer) -> some View {
        let isUpdate: Bool = { if case .update = offer { return true } else { return false } }()
        header(isUpdate ? "Update password for \(submission.origin.displayName)?"
                        : "Save password for \(submission.origin.displayName)?",
               symbol: "key.fill")
            .accessibilityAddTraits(.isHeader)
        Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
            GridRow {
                Text("Username").foregroundStyle(.secondary)
                if isUpdate {
                    Text(submission.username.isEmpty ? "No username" : submission.username)
                        .lineLimit(1).truncationMode(.middle)
                } else {
                    TextField("No username", text: $username)
                        .textFieldStyle(.roundedBorder)
                        .accessibilityLabel("Username")
                }
            }
            GridRow {
                Text("Password").foregroundStyle(.secondary)
                HStack(spacing: 6) {
                    Text(reveal ? submission.password : String(repeating: "•", count: min(max(submission.password.count, 8), 16)))
                        .font(.system(.body, design: .monospaced))
                        .lineLimit(1).truncationMode(.middle)
                        .textSelection(.disabled)
                    Spacer(minLength: 0)
                    Button {
                        reveal.toggle()
                    } label: {
                        Image(systemName: reveal ? "eye.slash" : "eye")
                    }
                    .buttonStyle(.borderless)
                    .help(reveal ? "Hide password" : "Show password")
                    .accessibilityLabel(reveal ? "Hide password" : "Show password")
                }
            }
        }
        .font(.callout)
        .onAppear { username = submission.username }
        Text("Stored in your Mac’s Keychain.")
            .font(.caption).foregroundStyle(.secondary)
        HStack {
            if !isUpdate {
                Button("Never for This Site") { passwords.neverSave(in: tab) }
                    .help("Don’t offer to save passwords on \(submission.origin.displayName)")
            }
            Spacer()
            Button("Not Now") { passwords.dismissPrompt(for: tab) }
            Button(isUpdate ? "Update" : "Save") {
                passwords.acceptSave(in: tab, username: isUpdate ? nil : username.trimmingCharacters(in: .whitespaces))
            }
            .buttonStyle(.borderedProminent)
        }
        .controlSize(.regular)
    }

    // MARK: Generator

    @ViewBuilder
    private func generate(_ password: String) -> some View {
        header("Use a strong password?", symbol: "key.viewfinder")
            .accessibilityAddTraits(.isHeader)
        HStack(spacing: 6) {
            Text(password)
                .font(.system(.body, design: .monospaced))
                .lineLimit(1).truncationMode(.middle)
                .textSelection(.enabled)
                .accessibilityLabel("Suggested password")
            Spacer(minLength: 0)
            Button {
                passwords.regenerate(in: tab)
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("Suggest another")
            .accessibilityLabel("Suggest another password")
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.06)))
        Text("Mosaic fills it in and offers to save it when you submit the form.")
            .font(.caption).foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
        HStack {
            Spacer()
            Button("Not Now") { passwords.dismissPrompt(for: tab) }
            Button("Use Password") { passwords.acceptGenerated(in: tab) }
                .buttonStyle(.borderedProminent)
        }
    }

    // MARK: Choose a login

    @ViewBuilder
    private func choose(_ origin: SiteOrigin, _ credentials: [PasswordCredential]) -> some View {
        header("Fill a password for \(origin.displayName)", symbol: "key.fill")
            .accessibilityAddTraits(.isHeader)
        VStack(spacing: 2) {
            ForEach(credentials) { credential in
                Button {
                    passwords.fill(in: tab, credential: credential)
                } label: {
                    HStack {
                        Image(systemName: "person.crop.circle").foregroundStyle(.secondary)
                        Text(credential.username.isEmpty ? "No username" : credential.username)
                            .lineLimit(1).truncationMode(.middle)
                        Spacer()
                    }
                    .padding(.vertical, 6).padding(.horizontal, 8)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.05)))
                .accessibilityLabel("Fill \(credential.username.isEmpty ? "login without username" : credential.username)")
            }
        }
        HStack {
            Button("Manage Passwords…") { passwords.openSettings() }
                .buttonStyle(.link)
            Spacer()
        }
    }
}

/// Mount point (area: passwords): the key button for the address bar. **The shell places
/// it at the trailing end of the address field** (Chrome/Safari put it there), in every
/// Mosaic. It is hidden (zero size) unless the selected tab's site has saved passwords;
/// it is tinted while the page shows a sign-in form. Click: fill (or choose a login) in a
/// popover; ⌘\ (Edit › Fill Password) does the same from the keyboard.
struct PasswordsToolbarButton: View {
    @EnvironmentObject private var model: BrowserModel
    @State private var showing = false

    var body: some View {
        let tab = model.selectedTab
        let credentials = model.passwords.fillable(in: tab)
        if !credentials.isEmpty {
            let active = model.passwords.offersFill(in: tab)
            Button {
                showing.toggle()
            } label: {
                Image(systemName: active ? "key.fill" : "key")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(active ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                    .frame(width: 24, height: 24)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("Fill a saved password (⌘\\)")
            .accessibilityLabel("Saved passwords for this site")
            .popover(isPresented: $showing, arrowEdge: .bottom) {
                PasswordsFillPopover(tab: tab, credentials: credentials, passwords: model.passwords) { showing = false }
            }
        }
    }
}

private struct PasswordsFillPopover: View {
    let tab: BrowserTab?
    let credentials: [PasswordCredential]
    let passwords: PasswordManager
    let close: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let origin = credentials.first?.origin {
                Text("Saved for \(origin.displayName)").font(.headline)
            }
            ForEach(credentials) { credential in
                Button {
                    close()
                    passwords.fill(in: tab, credential: credential)
                } label: {
                    Label(credential.username.isEmpty ? "No username" : credential.username, systemImage: "person.crop.circle")
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .padding(.vertical, 3)
                .accessibilityLabel("Fill \(credential.username.isEmpty ? "login without username" : credential.username)")
            }
            Divider()
            Button("Manage Passwords…") {
                close()
                passwords.openSettings()
            }
            .buttonStyle(.link)
        }
        .padding(14)
        .frame(minWidth: 240)
    }
}
