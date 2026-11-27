import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

// MARK: - Menu items

extension PageFeatures: NSMenuItemValidation {
    /// Adds the page-feature items to the File, Edit and View menus (called once from
    /// `AppDelegate.makeMainMenu`). Targets are this object, so AppDelegate needs no actions.
    func installMenuItems(in main: NSMenu) {
        func menu(_ title: String) -> NSMenu? { main.items.first { $0.title == title }?.submenu }
        func item(_ title: String, _ action: Selector, _ key: String = "",
                  _ modifiers: NSEvent.ModifierFlags = [.command]) -> NSMenuItem {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
            item.keyEquivalentModifierMask = modifiers
            item.target = self
            return item
        }

        if let file = menu("File") {
            file.addItem(.separator())
            file.addItem(item("Export as PDF…", #selector(menuExportPDF)))
            let screenshots = NSMenu(title: "Take Screenshot")
            screenshots.addItem(item("Save Visible Area…", #selector(menuSaveVisible)))
            screenshots.addItem(item("Save Full Page…", #selector(menuSaveFullPage)))
            screenshots.addItem(.separator())
            screenshots.addItem(item("Copy Visible Area", #selector(menuCopyVisible)))
            screenshots.addItem(item("Copy Full Page", #selector(menuCopyFullPage)))
            let screenshotItem = NSMenuItem(title: "Take Screenshot", action: nil, keyEquivalent: "")
            screenshotItem.submenu = screenshots
            file.addItem(screenshotItem)
            file.addItem(.separator())
            file.addItem(item("Print…", #selector(menuPrint), "p"))
        }

        if let edit = menu("Edit") {
            edit.addItem(.separator())
            let find = NSMenu(title: "Find")
            find.addItem(item("Find…", #selector(menuFind), "f"))
            find.addItem(item("Find Next", #selector(menuFindNext), "g"))
            find.addItem(item("Find Previous", #selector(menuFindPrevious), "g", [.command, .shift]))
            let findItem = NSMenuItem(title: "Find", action: nil, keyEquivalent: "")
            findItem.submenu = find
            edit.addItem(findItem)
        }

        if let view = menu("View") {
            view.addItem(.separator())
            view.addItem(item("Actual Size", #selector(menuResetZoom), "0"))
            view.addItem(item("Zoom In", #selector(menuZoomIn), "+"))
            // ⌘= is what people press for ⌘+ on most keyboards.
            let zoomInAlias = item("Zoom In", #selector(menuZoomIn), "=")
            zoomInAlias.isHidden = true
            zoomInAlias.allowsKeyEquivalentWhenHidden = true
            view.addItem(zoomInAlias)
            view.addItem(item("Zoom Out", #selector(menuZoomOut), "-"))
            view.addItem(.separator())
            view.addItem(item("Picture in Picture", #selector(menuPictureInPicture)))
            view.addItem(item("Developer Tools", #selector(menuDevTools), "i", [.command, .option]))
        }
    }

    @objc private func menuFind() { showFindBar() }
    @objc private func menuFindNext() { findNext() }
    @objc private func menuFindPrevious() { findNext(backwards: true) }
    @objc private func menuZoomIn() { zoomIn() }
    @objc private func menuZoomOut() { zoomOut() }
    @objc private func menuResetZoom() { resetZoom() }
    @objc private func menuPrint() { print() }
    @objc private func menuExportPDF() { exportPDF() }
    @objc private func menuDevTools() { toggleDevTools() }
    @objc private func menuPictureInPicture() { togglePictureInPicture() }
    @objc private func menuSaveVisible() { takeScreenshot(fullPage: false, to: .ask) }
    @objc private func menuSaveFullPage() { takeScreenshot(fullPage: true, to: .ask) }
    @objc private func menuCopyVisible() { takeScreenshot(fullPage: false, to: .clipboard) }
    @objc private func menuCopyFullPage() { takeScreenshot(fullPage: true, to: .clipboard) }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        let tab = selectedTab
        let onPage = isWebPage(tab)
        switch menuItem.action {
        case #selector(menuZoomIn):
            return onPage && (tab?.zoomFactor ?? 1) < PageZoom.maximum - 0.001
        case #selector(menuZoomOut):
            return onPage && (tab?.zoomFactor ?? 1) > PageZoom.minimum + 0.001
        case #selector(menuResetZoom):
            return onPage && !PageZoom.isDefault(tab?.zoomFactor ?? 1)
        case #selector(menuFindNext), #selector(menuFindPrevious):
            return onPage && !findText.isEmpty
        default:
            return onPage
        }
    }
}

// MARK: - Automation

extension PageFeatures {
    /// `page.*` commands of Automation.swift (scripted checks). `command` is the whole JSON
    /// command: "value" and, where a result is written, "out" (a file path).
    func automation(_ action: String, _ command: [String: Any]) {
        let value = command["value"] as? String ?? ""
        let out = command["out"] as? String
        func write(_ object: Any) {
            guard let out else { return }
            let data = (try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .fragmentsAllowed]))
                ?? Data("\(object)".utf8)
            try? data.write(to: URL(fileURLWithPath: out), options: .atomic)
        }
        let tab = selectedTab

        switch action {
        case "page.find":
            if !isFindBarVisible { showFindBar() }
            findText = value
        case "page.findNext": findNext()
        case "page.findPrevious": findNext(backwards: true)
        case "page.findCase":
            findMatchCase = value == "true"
        case "page.findClose": closeFindBar()
        case "page.zoomIn": zoomIn()
        case "page.zoomOut": zoomOut()
        case "page.zoomReset": resetZoom()
        case "page.print": print()
        case "page.pdf":
            if let tab { exportPDF(of: tab, to: URL(fileURLWithPath: value)) { write(["ok": $0]) } }
        case "page.devtools": toggleDevTools()
        case "page.devtoolsOpen":
            write(["open": tab?.chromiumView?.hasDevTools ?? false])
        case "page.mute": tab?.isMuted = value != "false"
        case "page.screenshot":
            // value: a file path, or "clipboard"; "full": "true" for the whole page.
            let full = (command["full"] as? String) == "true"
            let destination: ScreenshotDestination = value == "clipboard" ? .clipboard : .file(URL(fileURLWithPath: value))
            takeScreenshot(fullPage: full, to: destination) { png in
                let size = png.flatMap { NSBitmapImageRep(data: $0) }.map { ["width": $0.pixelsWide, "height": $0.pixelsHigh] }
                write(["ok": png != nil, "size": size ?? [:]])
            }
        case "page.permission":
            // value: allow | block | dismiss — answers the selected tab's first prompt.
            if let prompt = permissionPrompts.first(where: { $0.tabID == tab?.id }) {
                answer(prompt, with: value == "allow" ? .allow : value == "block" ? .block : nil)
            }
        case "page.setPermission":
            // value: "<site>|<permission>|<allow|block|forget>"
            let parts = value.split(separator: "|").map(String.init)
            if parts.count == 3, let permission = SitePermissionStore.Permission(rawValue: parts[1]) {
                permissions.set(SitePermissionStore.Decision(rawValue: parts[2]), for: permission, site: parts[0])
            }
        case "page.popup":
            // value: open | allow | dismiss
            if let notice = popupNotices.first(where: { $0.tabID == tab?.id }) {
                switch value {
                case "open": openPopups(notice, alwaysAllow: false)
                case "allow": openPopups(notice, alwaysAllow: true)
                default: dismissPopups(notice)
                }
            }
        case "page.dialog":
            // value: accept | cancel; "text": the prompt answer
            if let item = dialogs.first(where: { $0.tabID == tab?.id }) {
                finish(item, accepted: value == "accept", text: command["text"] as? String)
            }
        case "page.pip": togglePictureInPicture()
        case "page.fullscreenExit": endFullScreen()
        case "page.eval":
            // Chromium only: runs `value` in the page with a user gesture (test pages use
            // it to call APIs that need one); the result goes to "out".
            guard let view = tab?.chromiumView else { return write(["error": "not a Chromium tab"]) }
            view.executeDevToolsMethod(
                "Runtime.evaluate",
                params: ["expression": value, "userGesture": true, "returnByValue": true, "awaitPromise": true]
            ) { result, error in
                MainActor.assumeIsolated {
                    write(["result": (result?["result"] as? [String: Any])?["value"] ?? NSNull(), "error": error ?? NSNull(),
                           "exception": result?["exceptionDetails"] != nil])
                }
            }
        case "page.click":
            // value: "x,y[,button]" in page CSS pixels; button 0 left, 2 right. A real
            // (trusted) click, so it counts as a user gesture in both engines.
            let numbers = value.split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
            guard numbers.count >= 2, let tab else { return }
            click(tab, x: numbers[0], y: numbers[1], button: numbers.count > 2 ? Int(numbers[2]) : 0)
        case "page.gecko":
            // value: agent method; "args": JSON array of arguments; result to "out".
            let args = (command["args"] as? String).flatMap {
                try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [Any]
            } ?? []
            let id = tab?.geckoID
            Task {
                do {
                    let arguments: [Any] = (command["tabArg"] as? String) == "false" ? args : [id ?? -1] + args
                    let result = try await model.gecko.callWithArguments(value, arguments)
                    write(["result": result ?? NSNull()])
                } catch {
                    write(["error": error.localizedDescription])
                }
            }
        case "page.closePanels":
            // Cancels open/save panels and sheets (file pickers, print dialogs) so scripted
            // checks never leave one on screen; writes what it closed to "out".
            var closed: [String] = []
            for window in NSApp.windows where window.isVisible {
                if let panel = window as? NSSavePanel {
                    closed.append(panel.title.isEmpty ? String(describing: type(of: panel)) : panel.title)
                    panel.cancel(nil)
                } else if let sheet = window.attachedSheet {
                    closed.append(sheet.title.isEmpty ? String(describing: type(of: sheet)) : sheet.title)
                    window.endSheet(sheet, returnCode: .cancel)
                }
            }
            if let modal = NSApp.modalWindow {
                closed.append(modal.title)
                NSApp.abortModal()
                modal.orderOut(nil)
            }
            write(["closed": closed])
        case "page.renderOverlay":
            // value: PNG path. The overlays as they would draw over the content area (for
            // screenshots; window captures cannot include the test panel).
            renderOverlayForTesting(to: URL(fileURLWithPath: value))
        default:
            break
        }
    }

    private func click(_ tab: BrowserTab, x: Double, y: Double, button: Int) {
        switch tab.engine {
        case .chromium:
            guard let view = tab.chromiumView else { return }
            let name = button == 2 ? "right" : "left"
            for type in ["mousePressed", "mouseReleased"] {
                view.executeDevToolsMethod(
                    "Input.dispatchMouseEvent",
                    params: ["type": type, "x": x, "y": y, "button": name, "clickCount": 1]) { _, _ in }
            }
        case .gecko:
            if let id = tab.geckoID { model.gecko.send("synthesizeClick", id, x, y, button) }
        }
    }

    /// `dump` fields: what the page overlays show and each tab's page state.
    func automationState() -> [String: Any] {
        [
            "findBarVisible": isFindBarVisible,
            "findText": findText,
            "findMatchCase": findMatchCase,
            "findStatus": findStatus.map { ["current": $0.current, "total": $0.total, "label": $0.label] } ?? [:],
            "permissionPrompts": permissionPrompts.map { prompt in
                ["tab": index(of: prompt.tabID), "engine": prompt.engine.rawValue,
                 "sentence": prompt.request.sentence, "kinds": prompt.request.kinds.map(\.rawValue), "notes": prompt.notes]
            },
            "popupNotices": popupNotices.map { notice in
                ["tab": index(of: notice.tabID), "count": notice.count, "urls": notice.urls]
            },
            "dialogs": dialogs.map { item in
                ["tab": index(of: item.tabID), "kind": item.dialog.kind.rawValue,
                 "message": item.dialog.message, "origin": item.dialog.origin]
            },
            "fullScreenTab": model.tabs.firstIndex { $0.id == fullScreenTabID } ?? -1,
            "fullScreenWindow": fullScreen.windowNumber ?? -1,
            "sitePermissions": permissions.entries().map { "\($0.site) \($0.permission.rawValue) \($0.decision.rawValue)" },
            "siteZoom": zoomStore.sites.map { "\($0.site) \($0.factor)" },
            "tabs": model.tabs.map { tab in
                [
                    "zoom": tab.zoomFactor, "muted": tab.isMuted, "playingAudio": tab.isPlayingAudio,
                    "chromiumZoomLevel": tab.chromiumView?.zoomLevel ?? 0,
                    "chromiumMuted": tab.chromiumView?.audioMuted ?? false,
                    "mediaInUse": mediaInUse[tab.id].map { ["video": $0.video, "audio": $0.audio] } ?? [:],
                ] as [String: Any]
            },
        ]
    }

    private func index(of tabID: UUID) -> Int { model.tabs.firstIndex { $0.id == tabID } ?? -1 }

    /// Test runs only: until the shell places `FindBar`, draws it offscreen at the content
    /// area's size so scripted checks can picture the overlays.
    private func renderOverlayForTesting(to url: URL) {
        guard let window = NSApp.windows.first(where: { $0.title == "Mosaic" }), let content = window.contentView else { return }
        let size = NSSize(width: content.bounds.width - 12, height: content.bounds.height - 78 - 6)
        let offscreen = NSWindow(contentRect: NSRect(x: -20000, y: -20000, width: size.width, height: size.height),
                                 styleMask: [.borderless], backing: .buffered, defer: false)
        offscreen.isOpaque = false
        offscreen.backgroundColor = .clear
        offscreen.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: FindBar().environmentObject(model))
        host.frame = NSRect(origin: .zero, size: size)
        offscreen.contentView = host
        offscreen.orderBack(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            host.layoutSubtreeIfNeeded()
            if let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) {
                host.cacheDisplay(in: host.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: url)
            }
            offscreen.orderOut(nil)
            offscreen.close()
        }
    }
}
