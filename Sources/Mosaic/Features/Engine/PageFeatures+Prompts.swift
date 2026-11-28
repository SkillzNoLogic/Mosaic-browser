import AppKit
import CEFBridge
import MosaicCore

/// A permission request waiting for the person, shown under the toolbar while its tab is
/// selected.
struct PermissionPrompt: Identifiable {
    enum Token: Hashable {
        case gecko(Int)
        case chromium(UInt64)
    }

    let id = UUID()
    let tabID: UUID
    let engine: EngineKind
    let request: PermissionRequestInfo
    let token: Token
    /// The site of the page shown when it asked; leaving that site drops the prompt.
    let pageSite: String?
    /// Honest limits that apply to this request in this engine.
    let notes: [String]
}

/// Pop-ups a page tried to open without the person asking for them.
struct PopupNotice: Identifiable {
    let id = UUID()
    let tabID: UUID
    let engine: EngineKind
    let site: String?
    var urls: [String]
    var pageSite: String? { site }
    var count: Int
}

/// A JavaScript dialog of a Chromium page (Gecko draws its own, inside the page area).
struct PageDialog: Identifiable {
    let id = UUID()
    let tabID: UUID
    let dialog: MCEFJavaScriptDialog
}

extension PageFeatures {
    enum DropReason { case navigated, closed, engineSwitched }

    // MARK: - Permissions

    func handlePermissionRequest(_ request: PermissionRequestInfo, in tab: BrowserTab, token: PermissionPrompt.Token) {
        if let decision = request.automaticDecision(in: permissions) {
            respond(token, decision == .allow ? .allow : .deny, remembering: nil, site: request.site)
            return
        }
        let prompt = PermissionPrompt(
            tabID: tab.id, engine: tab.engine, request: request, token: token,
            pageSite: SitePermissionStore.site(for: tab.url),
            notes: Self.notes(for: request, engine: tab.engine))
        permissionPrompts.append(prompt)
        if tab === selectedTab, tab.engine == .gecko { activateForKeyboard() }
    }

    /// The person's answer: `.allow` / `.block` are remembered for the site (when the request
    /// can be); nil closes the prompt without deciding.
    func answer(_ prompt: PermissionPrompt, with decision: SitePermissionStore.Decision?) {
        permissionPrompts.removeAll { $0.id == prompt.id }
        var remembered: [SitePermissionStore.Permission] = []
        if let decision, prompt.request.isRememberable, let site = prompt.request.site {
            for permission in prompt.request.kinds.compactMap(\.storedAs) {
                permissions.set(decision, for: permission, site: site)
                remembered.append(permission)
            }
        }
        let response: Response = switch decision {
        case .allow: .allow
        case .block: .deny
        case nil: .dismiss
        }
        respond(prompt.token, response, remembering: decision.map { ($0, remembered) }, site: prompt.request.site)
    }

    func cancelPermissionPrompt(token: PermissionPrompt.Token) {
        permissionPrompts.removeAll { $0.token == token }
        if case .chromium(let id) = token { chromiumRequests[id] = nil }
    }

    enum Response { case allow, deny, dismiss }

    private func respond(
        _ token: PermissionPrompt.Token, _ response: Response,
        remembering: (SitePermissionStore.Decision, [SitePermissionStore.Permission])?, site: String?
    ) {
        switch token {
        case .chromium(let id):
            guard let request = chromiumRequests.removeValue(forKey: id) else { return }
            switch response {
            case .allow: request.allow()
            case .deny: request.deny()
            case .dismiss: request.dismiss()
            }
        case .gecko(let id):
            Task {
                // Gecko must know a remembered decision before the page hears the answer
                // (Notification.permission, repeat requests).
                if let (decision, stored) = remembering, let site {
                    let entries = stored.filter { [.location, .notifications].contains($0) }.map {
                        ["origin": site, "permission": $0.rawValue, "decision": decision.rawValue] as [String: Any]
                    }
                    if !entries.isEmpty { _ = try? await model.gecko.call("setSitePermissions", entries) }
                }
                let result = try? await model.gecko.call("answerPermission", id, response == .allow)
                if let message = result as? String { NSLog("Mosaic: Gecko permission answer failed: %@", message) }
            }
        }
    }

    private static func notes(for request: PermissionRequestInfo, engine: EngineKind) -> [String] {
        var notes: [String] = []
        let kinds = Set(request.kinds)
        switch engine {
        case .chromium:
            if kinds.contains(.notifications) {
                notes.append("Chromium tabs can’t display notifications in Mosaic. Open the site in a Mozilla tab to receive them.")
            }
            if kinds.contains(.location) {
                notes.append("macOS may also ask whether Mosaic may use your location.")
            }
            if !kinds.isDisjoint(with: [.camera, .microphone]) {
                notes.append("macOS may also ask whether Mosaic may use this device.")
            }
        case .gecko:
            if !kinds.isDisjoint(with: [.camera, .microphone]) {
                notes.append("macOS may also ask whether the Mozilla engine (Firefox) may use this device.")
            }
            if kinds.contains(.location) {
                notes.append("macOS may also ask whether the Mozilla engine (Firefox) may use your location.")
            }
        }
        if !request.isRememberable {
            notes.append("This permission is asked for every time.")
        }
        return notes
    }

    /// Keeps the engines' own memory in line with Mosaic's decisions (including edits made
    /// in Privacy settings).
    func mirror(_ permission: SitePermissionStore.Permission, _ decision: SitePermissionStore.Decision?, site: String) {
        if model.chromiumAvailable {
            let setting: MCEFContentSetting? = switch permission {
            case .location: .geolocation
            case .notifications: .notifications
            case .clipboard: .clipboard
            default: nil
            }
            if let setting {
                let value: MCEFContentSettingValue = switch decision {
                case .allow: .allow
                case .block: .block
                case nil: .default
                }
                MosaicCEF.setContentSetting(setting, value: value, forOrigin: site)
            }
        }
        if [.location, .notifications, .popups].contains(permission), model.gecko.isRunning {
            let entry: [String: Any] = ["origin": site, "permission": permission.rawValue,
                                        "decision": decision?.rawValue ?? NSNull()]
            model.gecko.send("setSitePermissions", [entry])
        }
    }

    // MARK: - Pop-ups

    /// `replacing`: the engine reported the page's complete list (Gecko), not one more (Chromium).
    func popupsBlocked(in tab: BrowserTab, urls: [String], count: Int, replacing: Bool = false) {
        let site = SitePermissionStore.site(for: tab.url)
        switch site.flatMap({ permissions.decision(for: .popups, site: $0) }) {
        case .allow:
            openPopups(urls: urls, from: tab)
            return
        case .block:
            return  // blocked on purpose: no notice
        case nil:
            break
        }
        if let index = popupNotices.firstIndex(where: { $0.tabID == tab.id }) {
            if replacing {
                popupNotices[index].urls = urls
                popupNotices[index].count = count
                return
            }
            popupNotices[index].urls += urls.filter { !popupNotices[index].urls.contains($0) }
            popupNotices[index].count += count
        } else {
            popupNotices.append(PopupNotice(tabID: tab.id, engine: tab.engine, site: site, urls: urls, count: count))
        }
    }

    /// "Open": opens the blocked pop-ups as tabs; `alwaysAllow` also remembers the site.
    func openPopups(_ notice: PopupNotice, alwaysAllow: Bool) {
        popupNotices.removeAll { $0.id == notice.id }
        if alwaysAllow, let site = notice.site { permissions.set(.allow, for: .popups, site: site) }
        guard let tab = tab(withID: notice.tabID) else { return }
        openPopups(urls: notice.urls, from: tab)
    }

    func dismissPopups(_ notice: PopupNotice) {
        popupNotices.removeAll { $0.id == notice.id }
    }

    private func openPopups(urls: [String], from tab: BrowserTab) {
        switch tab.engine {
        case .chromium:
            for url in urls { model.newTab(url: url, engine: .chromium) }
        case .gecko:
            // Gecko re-opens them itself (as tabs Mosaic then adopts), with their opener intact.
            if let id = tab.geckoID { model.gecko.send("openBlockedPopups", id) }
        }
    }

    // MARK: - JavaScript dialogs

    func showDialog(_ dialog: MCEFJavaScriptDialog, in tab: BrowserTab) {
        dialogs.append(PageDialog(tabID: tab.id, dialog: dialog))
    }

    func finish(_ dialog: PageDialog, accepted: Bool, text: String? = nil) {
        dialogs.removeAll { $0.id == dialog.id }
        dialog.dialog.finishAccepted(accepted, text: text)
        if dialog.tabID == selectedTab?.id { model.focusContent() }
    }

    func resetDialogs(in tab: BrowserTab) {
        let dropped = dialogs.filter { $0.tabID == tab.id }
        dialogs.removeAll { $0.tabID == tab.id }
        for item in dropped { item.dialog.finishAccepted(false, text: nil) }
    }

    // MARK: - Clean-up

    /// The page that asked has gone: its prompts are dismissed, its notices dropped. After a
    /// navigation only what belongs to other sites than `currentSite` goes (a request the
    /// new page made at once must survive).
    func dropPageState(forTab id: UUID, reason: DropReason, currentSite: String? = nil) {
        let stale: (UUID, String?) -> Bool = { tabID, site in
            tabID == id && (reason != .navigated || site != currentSite)
        }
        for prompt in permissionPrompts where stale(prompt.tabID, prompt.pageSite) {
            respond(prompt.token, .dismiss, remembering: nil, site: nil)
        }
        permissionPrompts.removeAll { stale($0.tabID, $0.pageSite) }
        popupNotices.removeAll { stale($0.tabID, $0.pageSite) }
        if reason != .navigated {
            // A navigation resets Chromium's dialogs itself (OnResetDialogState).
            let dropped = dialogs.filter { $0.tabID == id }
            dialogs.removeAll { $0.tabID == id }
            for item in dropped { item.dialog.finishAccepted(false, text: nil) }
        }
    }
}
