import AppKit
import SwiftUI
import CEFBridge
import MosaicCore

/// Automation commands and `dump` fields of the platform area (hooked into `Automation`).
///
///     runtimeInstall [file:///path/image.dmg]   install/update the private Gecko runtime
///                                               (default: download Firefox ESR from Mozilla)
///     runtimeCancel                             cancel an install in progress
///     runtimeCheck                              ask Mozilla for the current ESR version
///     runtimeRemove                             delete the private runtime
///     ruleAdd   example.com=chromium[,exact]    add or replace a per-site engine rule
///     ruleRemove example.com
///     ruleCurrent chromium                      "always open this site in …" for the current tab
///     defaultEngine gecko|chromium
///     languages en-GB,fr                        set web-content languages (or "system")
///     updateCheck / updateFrequency manual|daily
///     openSettings engine|extensions|languages|about|…   (empty value closes settings)
///     extensionToolbar <index>                  perform toolbar button <index>
///     previewPane <category> / previewScroll 0…1  show a settings page in its own window (screenshots)
@MainActor
enum PlatformAutomation {
    /// Returns true if `action` was a platform command.
    static func handle(_ action: String, _ command: [String: Any], model: BrowserModel) -> Bool {
        let value = command["value"] as? String ?? ""
        let platform = model.platform
        switch action {
        case "runtimeInstall":
            if value.isEmpty {
                platform.runtime.install()
            } else if let url = URL(string: value) {
                platform.runtime.install(from: url)
            }
        case "runtimeCancel": platform.runtime.cancel()
        case "runtimeCheck": Task { await platform.runtime.checkForUpdate() }
        case "runtimeRemove": Task { await platform.runtime.remove() }
        case "ruleAdd":
            let parts = value.split(separator: "=", maxSplits: 1).map(String.init)
            guard parts.count == 2 else { return true }
            let options = parts[1].split(separator: ",").map(String.init)
            if let kind = options.first.flatMap(EngineKind.init(rawValue:)) {
                platform.rules.set(domain: parts[0], engine: kind, includesSubdomains: !options.contains("exact"))
            }
        case "ruleRemove":
            let domain = EngineRuleSet.normalizedDomain(value)
            if let rule = platform.rules.rules.first(where: { $0.domain == domain }) { platform.rules.remove(rule) }
        case "ruleCurrent":
            if let kind = EngineKind(rawValue: value) { model.addEngineRuleForCurrentSite(kind) }
        case "defaultEngine":
            if let kind = EngineKind(rawValue: value) { model.setDefaultEngine(kind) }
        case "languages":
            if value == "system" {
                platform.languages.useSystemLanguages()
            } else {
                let tags = value.split(separator: ",").map(String.init)
                platform.languages.edit { $0 = LanguagePreferences(tags: tags) }
            }
        case "updateCheck": Task { await platform.updates.checkNow() }
        case "updateFrequency":
            if let frequency = UpdateCheckFrequency(rawValue: value) { platform.updates.frequency = frequency }
        case "openSettings":
            if let category = SettingsCategory(rawValue: value) { model.openSettings(category) } else { model.closeSettings() }
        case "previewPane":
            // Shows a settings page in a window of its own, for screenshots while the shell's
            // settings container is not in this branch. Never activates Mosaic.
            previewWindow?.close()
            guard let category = SettingsCategory(rawValue: value) else { return true }
            let window = NSWindow(contentRect: NSRect(x: 80, y: 80, width: 760, height: 900),
                                  styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            window.title = "Preview: \(category.title)"
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: SettingsPaneView(category: category).environmentObject(model))
            window.orderFront(nil)
            previewWindow = window
        case "previewScroll":
            // Scrolls the preview to a fraction (0…1) of its height.
            if let scroll = previewWindow?.contentView.flatMap(findScrollView), let fraction = Double(value),
               let document = scroll.documentView {
                let y = (document.bounds.height - scroll.contentView.bounds.height) * fraction
                scroll.contentView.scroll(to: NSPoint(x: 0, y: max(0, y)))
                scroll.reflectScrolledClipView(scroll.contentView)
            }
        case "extensionToolbar":
            let items = model.extensionToolbarItems
            if let index = Int(value), items.indices.contains(index) { items[index].perform() }
        default:
            return false
        }
        return true
    }

    private static var previewWindow: NSWindow?

    private static func findScrollView(_ view: NSView) -> NSScrollView? {
        if let scroll = view as? NSScrollView { return scroll }
        for child in view.subviews { if let found = findScrollView(child) { return found } }
        return nil
    }

    static func dump(_ model: BrowserModel) -> [String: Any] {
        let platform = model.platform
        let runtime = platform.runtime
        return [
            "runtime": [
                "phase": "\(runtime.phase)",
                "summary": runtime.phase.summary,
                "managedPath": runtime.managedAppURL.path,
                "installedVersion": runtime.installedRuntime?.version ?? "",
                "inUse": model.gecko.runtime?.appURL.path ?? "",
                "inUseVersion": model.gecko.runtime?.version ?? "",
                "latest": runtime.latestVersion?.original ?? "",
                "updateAvailable": runtime.isUpdateAvailable,
                "message": runtime.message ?? "",
                "missing": model.geckoRuntimeMissing,
            ] as [String: Any],
            "rules": platform.rules.rules.map {
                ["domain": $0.domain, "engine": $0.engine, "subdomains": $0.includesSubdomains] as [String: Any]
            },
            "defaultEngine": model.defaultEngine.rawValue,
            "switching": Dictionary(uniqueKeysWithValues: platform.switches.pending.map { ($0.key.uuidString, $0.value.target.rawValue) }),
            "lastSwitch": platform.switches.lastOutcome ?? "",
            "languages": platform.languages.preferences.tags,
            "languagesCustom": platform.languages.isCustom,
            "chromiumAcceptLanguages": MosaicCEF.acceptLanguageList ?? "",
            "languageErrors": [platform.languages.chromiumError, platform.languages.geckoError].compactMap { $0 },
            "updates": [
                "frequency": platform.updates.frequency.rawValue,
                "outcome": platform.updates.outcome?.message ?? "",
                "checking": platform.updates.isChecking,
            ] as [String: Any],
            "settingsCategory": model.settingsCategory?.rawValue ?? "",
            "previewWindow": previewWindow?.windowNumber ?? -1,
            "extensionPermissions": Dictionary(uniqueKeysWithValues: model.extensions.extensions.map { ext in
                let permissions = model.extensions.permissions(for: ext)
                return (ext.id, ["apis": permissions.apis, "sites": permissions.sites, "allSites": permissions.readsAllSites] as [String: Any])
            }),
            "extensionToolbar": model.extensionToolbarItems.map { ["id": $0.id, "name": $0.name, "running": $0.isRunning] },
        ]
    }
}
