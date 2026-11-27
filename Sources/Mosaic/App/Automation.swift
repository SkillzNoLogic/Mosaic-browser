import AppKit
import MosaicCore

/// Scripted control for end-to-end checks, off unless Mosaic is launched with
/// `--mosaic-automation`. Commands arrive as distributed notifications named
/// `dev.mosaic.browser.automation` whose object is a JSON dictionary, e.g.
///
///     {"do": "navigate", "value": "example.com"}
///     {"do": "dump", "value": "/tmp/state.json"}
///
/// See Scripts/drive.swift for a sender.
@MainActor
final class Automation {
    static let flag = "--mosaic-automation"
    /// Every running copy would hear a shared name, so `MOSAIC_AUTOMATION_ID` (also read by
    /// Scripts/drive.swift) gives each copy its own channel.
    static let notification = Notification.Name(
        "dev.mosaic.browser.automation"
            + (ProcessInfo.processInfo.environment["MOSAIC_AUTOMATION_ID"].map { $0.isEmpty ? "" : ".\($0)" } ?? ""))

    private let model: BrowserModel
    private weak var window: NSWindow?
    private var observer: NSObjectProtocol?

    init(model: BrowserModel, window: NSWindow) {
        self.model = model
        self.window = window
        model.allowsFocusChanges = false
        observer = DistributedNotificationCenter.default().addObserver(
            forName: Self.notification, object: nil, queue: .main
        ) { [weak self] note in
            let payload = note.object as? String
            MainActor.assumeIsolated { self?.handle(payload) }
        }
    }

    private func handle(_ payload: String?) {
        guard let data = payload?.data(using: .utf8),
              let command = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let action = command["do"] as? String
        else { return }
        let value = command["value"] as? String ?? ""

        switch action {
        case "navigate": model.navigate(value)
        case "newTab": model.newTab(url: value)
        case "closeTab": model.closeSelectedTab()
        case "selectTab":
            if let index = Int(value), model.tabs.indices.contains(index) { model.select(model.tabs[index]) }
        case "engine":
            if let kind = EngineKind(rawValue: value) { model.setEngine(kind) }
        case "toggleEngine": model.toggleEngine()
        case "searchEngine":
            if let engine = SearchEngine.named(value) { model.searchEngine = engine }
        case "back": model.goBack()
        case "forward": model.goForward()
        case "reload": model.reload()
        case "installExtension": Task { await model.extensions.install(fromWebStore: value) }
        case "installFile": Task { await model.extensions.install(file: URL(fileURLWithPath: value)) }
        case "removeExtension":
            if let ext = model.extensions.extensions.first(where: { $0.id == value }) {
                Task { await model.extensions.remove(ext) }
            }
        case "extensionPage":
            let parts = value.split(separator: ":").map(String.init)
            if parts.count == 2, let ext = model.extensions.extensions.first(where: { $0.id == parts[0] }) {
                model.openExtensionPage(ext, kind: parts[1])
            }
        case "showExtensions": model.showExtensions = (value != "false")
        case "blocker": model.setBlockerEnabled(value != "off")
        case "blockerSite":
            if let tab = model.selectedTab { model.setBlocking(value != "off", onSiteOf: tab) }
        case "showBlocker": model.showBlocker = (value != "false")
        case "updateLists": Task { await model.blocker.updateLists() }
        case "geckoEval":
            // {"do":"geckoEval","value":"<agent method>","out":"/path"}
            let out = command["out"] as? String
            Task {
                let result = try? await model.gecko.call(value)
                if let out {
                    try? "\(result ?? "nil")".write(toFile: out, atomically: true, encoding: .utf8)
                }
            }
        case "allowFocus": model.allowsFocusChanges = (value == "true")
        case "activate": NSApp.activate(ignoringOtherApps: true)
        case "orderFront": window?.orderFront(nil)
        case "hitTest": hitTest(to: value)
        case let action where action.hasPrefix("passwords"):  // passwords: see PasswordsAutomation.swift
            model.passwords.automation(action, value: value, command: command)
        case "dump": dump(to: value)
        case "quit": NSApp.terminate(nil)
        default: break
        }
    }

    /// Asks the window server which window a click would land on, at the
    /// centre of the content area and in the middle of the toolbar.
    private func hitTest(to path: String) {
        guard let window, let content = window.contentView else { return }
        let frame = window.frame
        let contentCentre = NSPoint(x: frame.midX, y: frame.minY + (frame.height - 78) / 2)
        let toolbar = NSPoint(x: frame.midX, y: frame.maxY - 58)
        let stack = GeckoEngine.onScreenWindows().prefix(6).map { ["number": $0.number, "pid": Int($0.pid)] }
        let result: [String: Any] = [
            "mosaicWindow": window.windowNumber,
            "geckoWindow": model.gecko.windowNumber() ?? -1,
            "atContentCentre": NSWindow.windowNumber(at: contentCentre, belowWindowWithWindowNumber: 0),
            "atToolbar": NSWindow.windowNumber(at: toolbar, belowWindowWithWindowNumber: 0),
            "stackFrontToBack": Array(stack),
            "contentViewIsOpaque": content.isOpaque,
            "windowIsOpaque": window.isOpaque,
        ]
        if let data = try? JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
        }
    }

    private func dump(to path: String) {
        let frame = window?.frame ?? .zero
        let state: [String: Any] = [
            "tabs": model.tabs.map { tab in
                [
                    "engine": tab.engine.rawValue, "url": tab.url, "title": tab.title,
                    "loading": tab.isLoading, "canGoBack": tab.canGoBack, "canGoForward": tab.canGoForward,
                    "geckoID": tab.geckoID ?? -1, "hasChromiumView": tab.chromiumView != nil,
                    "blocked": tab.blockedCount,
                    "failure": tab.failure ?? "", "selected": tab.id == model.selectedTabID,
                ] as [String: Any]
            },
            "engine": model.engine.rawValue,
            "searchEngine": model.searchEngine.id,
            "addressText": model.addressText,
            "chromiumAvailable": model.chromiumAvailable,
            "geckoStatus": "\(model.gecko.status)",
            "geckoPID": Int(model.gecko.processIdentifier ?? -1),
            "geckoDocked": model.isGeckoDocked,
            "geckoWindow": model.gecko.windowNumber() ?? -1,
            "window": ["number": window?.windowNumber ?? -1, "x": frame.minX, "y": frame.minY,
                       "width": frame.width, "height": frame.height],
            "isActive": NSApp.isActive,
            "notice": model.notice ?? "",
            "extensions": model.extensions.extensions.map { ext in
                [
                    "id": ext.id, "geckoID": ext.geckoID, "name": ext.name, "version": ext.version,
                    "enabled": ext.enabled, "loaded": model.extensions.loaded.contains(ext.geckoID),
                    "notes": ext.notes.map { "\($0.severity.rawValue): \($0.message)" },
                ] as [String: Any]
            },
            "blocker": [
                "status": "\(model.blocker.status)", "enabled": model.blocker.isEnabled,
                "allowlist": model.blocker.settings.allowlist.sorted(),
                "engineVersion": model.blocker.engineVersion ?? "",
                "updating": model.blocker.isUpdating,
            ] as [String: Any],
            "extensionError": model.extensions.errorMessage ?? "",
            "extensionsBusy": model.extensions.isBusy,
            "passwords": model.passwords.automationState(),  // passwords (never includes a password)
        ]
        if let data = try? JSONSerialization.data(withJSONObject: state, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
        }
    }
}
