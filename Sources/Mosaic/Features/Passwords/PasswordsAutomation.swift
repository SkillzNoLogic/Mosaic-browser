import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

/// Scripted checks for passwords (area: passwords). Commands arrive through `Automation`
/// (any action starting with "passwords"); state appears under "passwords" in `dump`.
///
///     passwordsFill [username]            fill the selected tab (as ⌘\ does)
///     passwordsPrompt save|never|notnow|use|regenerate
///     passwordsSuggest                    Edit › Suggest Strong Password
///     passwordsSettings                   open Settings › Passwords & Autofill
///     passwordsOption offerToSave=on|off, suggestStrongPasswords=on|off
///     passwordsPrivate on|off             treat the selected tab as private
///     passwordsNeverSave <origin>|on|off
///     passwordsVerify <origin>|<user>|<password>  out: "match", "mismatch" or "missing"
///     passwordsAudit                      out: {"weak": n, "reused": n}
///     passwordsSnapshot prompt|button|settings    out: PNG of that view, rendered off screen
///   Isolated profiles only (they write to that profile's own Keychain scope):
///     passwordsAdd <origin>|<user>|<password>
///     passwordsDelete <origin>|<user>
///     passwordsDeleteAll
///     passwordsImport <csv path>          out: summary
///     passwordsExport <csv path>
///
/// Neither the commands nor `dump` ever write a saved password anywhere.
extension PasswordManager {
    func automation(_ action: String, value: String, command: [String: Any]) {
        let out = command["out"] as? String
        let parts = value.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
        let tab = model?.selectedTab
        let isolated = AppPaths.isIsolatedProfile

        func write(_ text: String) {
            guard let out else { return }
            try? text.write(toFile: out, atomically: true, encoding: .utf8)
        }
        func id(_ origin: String, _ user: String) -> CredentialID? {
            SiteOrigin(userInput: origin).map { CredentialID(origin: $0, username: user) }
        }

        switch action {
        case "passwordsFill":
            let credential = value.isEmpty ? nil : fillable(in: tab).first { $0.username == value }
            fill(in: tab, credential: credential)
        case "passwordsPrompt":
            guard let tab else { return }
            switch value {
            case "save": acceptSave(in: tab)
            case "never": neverSave(in: tab)
            case "notnow": dismissPrompt(for: tab)
            case "use": acceptGenerated(in: tab)
            case "regenerate": regenerate(in: tab)
            default: break
            }
        case "passwordsSuggest": offerGeneratedPassword(in: tab)
        case "passwordsSettings": openSettings()
        case "passwordsOption":
            let pair = value.split(separator: "=").map(String.init)
            guard pair.count == 2 else { return }
            let on = pair[1] == "on"
            vault.update { preferences in
                if pair[0] == "offerToSave" { preferences.offerToSave = on }
                if pair[0] == "suggestStrongPasswords" { preferences.suggestStrongPasswords = on }
            }
            changed()
        case "passwordsPrivate":
            guard let tab else { return }
            if value == "on" { simulatedPrivateTabs.insert(tab.id) } else { simulatedPrivateTabs.remove(tab.id) }
        case "passwordsNeverSave":
            guard parts.count == 2, let origin = SiteOrigin(userInput: parts[0]) else { return }
            vault.setNeverSave(parts[1] == "on", for: origin)
            changed()
        case "passwordsVerify":
            guard parts.count == 3, let id = id(parts[0], parts[1]) else { return write("invalid") }
            guard let saved = try? vault.password(for: id) else { return write("missing") }
            write(saved == parts[2] ? "match" : "mismatch")
        case "passwordsAudit":
            let audit = vault.audit()
            write("{\"weak\": \(audit.weak.count), \"reused\": \(audit.reused.count)}")
        case "passwordsSnapshot":
            snapshot(value, to: out)
        case "passwordsAdd" where isolated:
            guard parts.count == 3, let id = id(parts[0], parts[1]) else { return }
            do { try vault.save(id, password: parts[2]) } catch { write("error: \(error.localizedDescription)") }
            changed()
        case "passwordsDelete" where isolated:
            guard parts.count == 2, let id = id(parts[0], parts[1]) else { return }
            try? vault.delete(id)
            changed()
        case "passwordsDeleteAll" where isolated:
            for credential in vault.credentials { try? vault.delete(credential.id) }
            changed()
        case "passwordsImport" where isolated:
            do {
                let text = try String(contentsOfFile: value, encoding: .utf8)
                let summary = vault.importEntries(try PasswordCSV.parseExport(text))
                write("added \(summary.added) updated \(summary.updated) unchanged \(summary.unchanged) failed \(summary.failed) skipped \(summary.skipped.count)")
            } catch {
                write("error: \(error.localizedDescription)")
            }
            changed()
        case "passwordsExport" where isolated:
            if let entries = try? vault.exportEntries() {
                FileManager.default.createFile(atPath: value, contents: Data(PasswordCSV.export(entries).utf8),
                                               attributes: [.posixPermissions: 0o600])
            }
        default:
            break
        }
    }

    /// The "passwords" section of `dump`. Never contains a password.
    func automationState() -> [String: Any] {
        let iso = ISO8601DateFormatter()
        var tabs: [[String: Any]] = []
        for (index, tab) in (model?.tabs ?? []).enumerated() {
            let page = pages[tab.id]
            var entry: [String: Any] = [
                "index": index,
                "hasLogin": page?.hasLogin ?? false,
                "hasSignup": page?.hasSignup ?? false,
                "pending": page?.pending != nil,
                "fillable": fillable(in: tab).count,
                "offersFill": offersFill(in: tab),
                "private": isPrivate(tab),
            ]
            switch prompts[tab.id] {
            case .save(let submission, let offer)?:
                entry["prompt"] = { if case .update = offer { return "update" } else { return "save" } }()
                entry["promptOrigin"] = submission.origin.string
                entry["promptUsername"] = submission.username
            case .generate(let origin, let password)?:
                entry["prompt"] = "generate"
                entry["promptOrigin"] = origin.string
                // Its strength only: the suggestion is not saved anywhere yet, but stays out of files.
                entry["promptPasswordLength"] = password.count
            case .choose(let origin, let credentials)?:
                entry["prompt"] = "choose"
                entry["promptOrigin"] = origin.string
                entry["promptChoices"] = credentials.map(\.username)
            case nil:
                entry["prompt"] = ""
            }
            tabs.append(entry)
        }
        var state: [String: Any] = [
            "keychainScope": vault.store is KeychainPasswordStore ? Self.keychainScope.securityDomain : "memory",
            "count": vault.credentials.count,
            "credentials": vault.credentials.map { credential in
                ["origin": credential.origin.string, "username": credential.username,
                 "lastUsed": vault.lastUsed(credential.id).map(iso.string(from:)) ?? ""]
            },
            "neverSave": vault.preferences.neverSave.map(\.string),
            "offerToSave": vault.preferences.offerToSave,
            "suggestStrongPasswords": vault.preferences.suggestStrongPasswords,
            "tabs": tabs,
            "error": vault.lastError ?? "",
            "chromiumScriptLoaded": MosaicCEF.passwordPageScript != nil,
        ]
        if let lastFill {
            state["lastFill"] = ["origin": lastFill.origin, "count": lastFill.count, "at": iso.string(from: lastFill.date)]
        }
        if let lastSubmission {
            state["lastSubmission"] = ["origin": lastSubmission.origin, "username": lastSubmission.username,
                                       "mode": lastSubmission.mode, "confirmed": lastSubmission.confirmed]
        }
        return state
    }

    /// Renders one of the password views into a PNG, in a window that is never shown, so the
    /// views can be checked without the shell having placed them yet.
    private func snapshot(_ which: String, to path: String?) {
        guard let path, let model else { return }
        let content: AnyView
        let size: CGSize
        switch which {
        case "prompt":
            content = AnyView(PasswordPromptOverlay().frame(width: 380, height: 300, alignment: .topTrailing))
            size = CGSize(width: 380, height: 300)
        case "button":
            content = AnyView(PasswordsToolbarButton().padding(8))
            size = CGSize(width: 60, height: 44)
        default:
            content = AnyView(PasswordsSettingsPane())
            size = CGSize(width: 760, height: 1500)
        }
        let host = NSHostingView(rootView: content.environmentObject(model)
            .background(Color(nsColor: .windowBackgroundColor)))
        host.frame = CGRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        host.layoutSubtreeIfNeeded()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
            host.layoutSubtreeIfNeeded()
            if let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds) {
                host.cacheDisplay(in: host.bounds, to: bitmap)
                try? bitmap.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
            }
            window.contentView = nil
            window.close()
        }
    }
}
