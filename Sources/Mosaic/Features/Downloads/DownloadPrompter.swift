import AppKit

/// Questions about downloads (duplicate file, Free Download Manager, safety, where to
/// save), shown one at a time as sheets on Mosaic's window, so they never cover another
/// app. Scripted runs answer them with the `downloadPromptAnswer` / `downloadPromptSave`
/// automation commands.
@MainActor
final class DownloadPrompter {
    struct Pending {
        let kind: String
        let title: String
        let buttons: [String]
    }

    private(set) var pending: Pending?
    private var currentSheet: NSWindow?
    private var currentSavePanel: NSSavePanel?
    /// Set by `save(as:)` (automation): the path to use instead of what the panel shows.
    private var scriptedDestination: URL?
    private var chain: Task<Void, Never>?

    /// Mosaic's main window (the shell names it "MosaicMainWindow").
    private var window: NSWindow? {
        NSApp.windows.first { $0.frameAutosaveName == "MosaicMainWindow" && $0.isVisible }
            ?? NSApp.mainWindow ?? NSApp.windows.first { $0.isVisible && $0.canBecomeMain }
    }

    /// Shows an alert and returns the index of the button pressed (0 = first), or nil when
    /// there is no window to show it on.
    func ask(
        kind: String, title: String, message: String, buttons: [String], style: NSAlert.Style = .informational,
        destructiveIndex: Int? = nil
    ) async -> Int? {
        await serialised {
            guard let window = self.window else { return nil }
            let alert = NSAlert()
            alert.alertStyle = style
            alert.messageText = title
            alert.informativeText = message
            for (index, title) in buttons.enumerated() {
                let button = alert.addButton(withTitle: title)
                if index == destructiveIndex { button.hasDestructiveAction = true }
            }
            // The last button is always the cautious choice: Escape picks it, and for a
            // safety warning so does Return.
            if style == .warning {
                alert.buttons.first?.keyEquivalent = ""
                alert.buttons.last?.keyEquivalent = "\r"
            } else {
                alert.buttons.last?.keyEquivalent = "\u{1b}"
                if buttons.count > 1 { alert.buttons.first?.keyEquivalent = "\r" }
            }
            self.pending = Pending(kind: kind, title: title, buttons: buttons)
            self.currentSheet = alert.window
            defer {
                self.pending = nil
                self.currentSheet = nil
            }
            let response = await withCheckedContinuation { continuation in
                alert.beginSheetModal(for: window) { continuation.resume(returning: $0) }
            }
            let index = response.rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue
            return buttons.indices.contains(index) ? index : buttons.count - 1
        }
    }

    /// "Save As" sheet. Returns the chosen file, or nil if cancelled.
    func chooseDestination(suggestedName: String, directory: URL) async -> URL? {
        await serialised {
            guard let window = self.window else { return nil }
            let panel = NSSavePanel()
            panel.title = "Save Download"
            panel.prompt = "Save"
            panel.directoryURL = directory
            panel.nameFieldStringValue = suggestedName
            panel.canCreateDirectories = true
            panel.isExtensionHidden = false
            panel.allowsOtherFileTypes = true
            self.pending = Pending(kind: "save", title: suggestedName, buttons: ["Save", "Cancel"])
            self.currentSavePanel = panel
            defer {
                self.pending = nil
                self.currentSavePanel = nil
            }
            let response = await withCheckedContinuation { continuation in
                panel.beginSheetModal(for: window) { continuation.resume(returning: $0) }
            }
            let scripted = self.scriptedDestination
            self.scriptedDestination = nil
            return response == .OK ? (scripted ?? panel.url) : nil
        }
    }

    // MARK: Automation

    /// Presses button `index` of the alert on screen (for the save sheet: 0 = Save, 1 = Cancel).
    func answer(_ index: Int) {
        if let panel = currentSavePanel, let window = panel.sheetParent {
            window.endSheet(panel, returnCode: index == 0 ? .OK : .cancel)
        } else if let sheet = currentSheet, let parent = sheet.sheetParent {
            parent.endSheet(sheet, returnCode: NSApplication.ModalResponse(
                rawValue: NSApplication.ModalResponse.alertFirstButtonReturn.rawValue + index))
        }
    }

    /// Saves the open "Save As" sheet as `path`.
    func save(as path: String) {
        guard let panel = currentSavePanel, let window = panel.sheetParent else { return }
        scriptedDestination = URL(fileURLWithPath: path)
        window.endSheet(panel, returnCode: .OK)
    }

    /// Runs prompts one after another.
    private func serialised<T: Sendable>(_ body: @escaping @MainActor () async -> T) async -> T {
        let previous = chain
        let task = Task { @MainActor () -> T in
            await previous?.value
            return await body()
        }
        chain = Task { _ = await task.value }
        return await task.value
    }
}
