import AppKit
import MosaicCore

/// Automation commands for the productivity area (sent with Scripts/drive.swift; routed
/// here from `Automation.handle`):
///
///     palette            open | close | toggle
///     paletteQuery       <query> [out]        sets the query; writes the results to `out`
///     palettePerform     <action id> | selection
///     reader             toggle | on | off    for the selected tab
///     readerSettings     {"fontSize":7,"theme":"sepia",…}
///     setShortcut        <command id>=<cmd+shift+k> [out]   fails on a conflict (reason in
///                        `out`); end with "!" to take the shortcut from the other command;
///                        "<id>=" removes the shortcut
///     resetShortcut      <command id> | all
///     performShortcut    <command id>         runs the menu command
///     speedDial          {"op":"add","title":"…","url":"…"} · {"op":"edit","url":"…","title":"…","newURL":"…"}
///                        {"op":"remove","url":"…"} · {"op":"move","url":"…","to":0} · {"op":"hide","url":"…"}
///                        {"op":"restoreHidden"} · {"op":"showTopSites","value":false} · {"op":"showIcons","value":false}
///     showShortcutsSettings                  opens Settings › Shortcuts & Gestures
///     pageScript         <JavaScript> <out>   evaluates in the selected Chromium tab
///     pageScreenshot     <out.png>            the selected Chromium page as rendered (works while
///                        the window is covered, unlike a window capture)
///
/// The state (palette, reader per tab, shortcuts, Speed Dial) is in `dump` under "productivity".
extension Productivity {
    /// Returns false when `action` is not one of this area's commands.
    func handleAutomation(_ action: String, value: String, command: [String: Any]) -> Bool {
        let out = command["out"] as? String
        switch action {
        case "palette":
            switch value {
            case "close": palette.dismiss(restoringFocus: false)
            case "toggle": palette.toggle()
            default: if !palette.isPresented { palette.present() }
            }
        case "paletteQuery":
            palette.query = value
            if let out { write(paletteState(limit: 60), to: out) }
        case "palettePerform":
            if value == "selection" { palette.performSelection() } else if !palette.perform(id: value), let out {
                write(["error": "no action \(value)"], to: out)
            }
        case "reader":
            guard let tab = model.selectedTab else { break }
            switch value {
            case "on": reader.setActive(true, for: tab)
            case "off": reader.setActive(false, for: tab)
            default: reader.toggle(tab)
            }
        case "readerSettings":
            guard let object = (try? JSONSerialization.jsonObject(with: Data(value.utf8))) as? [String: Any] else { break }
            var merged = (try? JSONSerialization.jsonObject(with: JSONEncoder().encode(reader.settings))) as? [String: Any] ?? [:]
            merged.merge(object) { $1 }
            if let data = try? JSONSerialization.data(withJSONObject: merged),
               let settings = try? JSONDecoder().decode(ReaderSettings.self, from: data) {
                reader.update(settings.clamped)
            }
        case "setShortcut":
            let result = setShortcut(value)
            if let out { write(["result": result], to: out) }
        case "resetShortcut":
            if value == "all" {
                shortcuts.resetAll()
            } else if let command = shortcuts.command(id: value) {
                shortcuts.reset(command)
            }
        case "performShortcut":
            shortcuts.perform(commandID: value)
        case "speedDial":
            let result = speedDialEdit(value)
            if let out { write(["result": result], to: out) }
        case "showShortcutsSettings":
            model.openSettings(.shortcuts)
        case "pageScript":
            guard let view = model.selectedTab?.chromiumView else {
                if let out { write(["error": "no Chromium page"], to: out) }
                break
            }
            view.evaluatePageScript(value) { [weak self] result, error in
                MainActor.assumeIsolated {
                    if let out { self?.write(["value": result ?? NSNull(), "error": error ?? NSNull()], to: out) }
                }
            }
        case "pageScreenshot":
            model.selectedTab?.chromiumView?.callDevToolsMethod("Page.captureScreenshot", params: ["format": "png"]) { result, _ in
                guard let base64 = result?["data"] as? String, let data = Data(base64Encoded: base64) else { return }
                try? data.write(to: URL(fileURLWithPath: value))
            }
        default:
            return false
        }
        return true
    }

    /// For `dump`.
    var automationState: [String: Any] {
        [
            "palette": paletteState(limit: 15),
            "reader": reader.automationState,
            "shortcuts": shortcuts.commands.map { command in
                [
                    "id": command.id, "group": command.group, "title": command.title,
                    "default": command.defaultCombo?.displayString ?? "",
                    "current": shortcuts.combo(for: command)?.displayString ?? "",
                    "menuItem": shortcuts.menuCombo(for: command.id)?.displayString ?? "",
                    "customised": shortcuts.bindings.isCustomised(command),
                ] as [String: Any]
            },
            "geckoForwarding": shortcuts.geckoForwardingPairs(),
            "speedDial": [
                "pinned": speedDial.dial.pinned.map { ["title": $0.title, "url": $0.url] },
                "hiddenTopSites": speedDial.dial.hiddenTopSites.sorted(),
                "showsTopSites": speedDial.dial.showsTopSites,
                "showsIcons": speedDial.dial.showsIcons,
            ] as [String: Any],
        ]
    }

    private func paletteState(limit: Int) -> [String: Any] {
        [
            "presented": palette.isPresented,
            "query": palette.query,
            "selection": palette.selection,
            "results": palette.results.prefix(limit).map { action in
                [
                    "id": action.id, "title": action.title, "subtitle": action.subtitle ?? "",
                    "kind": action.kind.rawValue, "shortcut": action.shortcut?.displayString ?? "",
                ] as [String: Any]
            },
        ]
    }

    private func setShortcut(_ value: String) -> String {
        guard let separator = value.lastIndex(of: "=") else { return "expected <id>=<shortcut>" }
        let id = String(value[..<separator])
        var text = String(value[value.index(after: separator)...])
        let replace = text.hasSuffix("!") && text.count > 1
        if replace { text.removeLast() }
        guard let command = shortcuts.command(id: id) else { return "no command \(id)" }
        if text.isEmpty {
            shortcuts.clear(command)
            return "cleared"
        }
        guard let combo = KeyCombo(string: text) else { return "cannot parse \(text)" }
        switch shortcuts.problem(with: combo, for: command) {
        case nil:
            shortcuts.assign(combo, to: command)
            return "ok"
        case .conflict(let ids) where replace:
            shortcuts.assign(combo, to: command)
            return "ok, replaced \(ids.joined(separator: ", "))"
        case .conflict(let ids):
            return "conflict: \(ids.joined(separator: ", "))"
        case .needsModifier:
            return "needs a modifier"
        case .reserved(let reason):
            return "reserved by macOS for \(reason)"
        }
    }

    private func speedDialEdit(_ value: String) -> String {
        guard let edit = (try? JSONSerialization.jsonObject(with: Data(value.utf8))) as? [String: Any],
              let op = edit["op"] as? String else { return "bad JSON" }
        let url = edit["url"] as? String ?? ""
        let site = speedDial.dial.pinned.first { SpeedDial.identity(of: $0.url) == SpeedDial.identity(of: SpeedDial.normalizedURL(url) ?? url) }
        do {
            switch op {
            case "add": try speedDial.add(title: edit["title"] as? String ?? "", url: url)
            case "edit":
                guard let site else { return "not pinned" }
                try speedDial.update(site.id, title: edit["title"] as? String ?? site.title, url: edit["newURL"] as? String ?? site.url)
            case "remove":
                guard let site else { return "not pinned" }
                speedDial.remove(site.id)
            case "move":
                guard let site, let to = edit["to"] as? Int else { return "not pinned" }
                speedDial.move(site.id, to: to)
            case "hide": speedDial.hideTopSite(SpeedDialSite(title: "", url: url))
            case "restoreHidden": speedDial.restoreHiddenTopSites()
            case "showTopSites": speedDial.setShowsTopSites(edit["value"] as? Bool ?? true)
            case "showIcons": speedDial.setShowsIcons(edit["value"] as? Bool ?? true)
            default: return "unknown op \(op)"
            }
            return "ok"
        } catch {
            return SpeedDialModel.message(for: error)
        }
    }

    private func write(_ object: [String: Any], to path: String) {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]) else { return }
        try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
    }
}
