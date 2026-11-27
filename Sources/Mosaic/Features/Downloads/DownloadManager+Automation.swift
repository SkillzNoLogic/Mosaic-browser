import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

/// Scripted control of downloads (see `Automation`). Every command starts with "download":
///
///     downloadsFolder <path>                     where downloads are saved
///     downloadsSetting <key>=<value>             askWhereToSave, openSafeFiles, notify, organize,
///                                                fdmMode (off|ask|always), fdmMinimumMB, fdmCookies, fdmContextMenu
///     download <url>                             start a download in Chromium (falls back to Gecko)
///     downloadGecko <url>                        start a download in Gecko
///     downloadPause|downloadResume|downloadCancel|downloadRetry|downloadRemove|downloadOpen|
///     downloadReveal|downloadKeep|downloadTrash|downloadWithFDM <item>
///                                                <item>: index in the list (0 = newest) or a file name
///     downloadLinkToFDM <url>                    as the link context-menu item does
///     downloadsClear                             clear finished entries
///     downloadPromptAnswer <n>                   press button n of the open download question
///     downloadPromptSave <path>                  save the open "Save As" sheet as <path>
///     downloadsPopover true|false                show or hide the toolbar popover
///     downloadsPage                              open mosaic://downloads
///     downloadsSearch <text>                     the Downloads page's search field
///     downloadsSnapshot <png>                    render the popover, page and settings to PNGs
extension DownloadManager {
    func handleAutomation(_ action: String, value: String, command: [String: Any]) {
        switch action {
        case "downloadsFolder":
            settings.folderPath = (value as NSString).expandingTildeInPath
        case "downloadsSetting":
            applySetting(value)
        case "download":
            startInChromium(url: value)
        case "downloadGecko":
            startInGecko(url: value)
        case "downloadLinkToFDM":
            sendLinkToFDM(url: value, referrer: nil, from: model?.engine ?? .chromium)
        case "downloadsClear":
            clearFinished()
        case "downloadPromptAnswer":
            prompter.answer(Int(value) ?? 0)
        case "downloadPromptSave":
            prompter.save(as: (value as NSString).expandingTildeInPath)
        case "downloadsPopover":
            isPopoverShown = value != "false"
        case "downloadsPage":
            openDownloadsPage()
        case "downloadsSearch":
            searchText = value
        case "downloadsSnapshot":
            snapshot(to: value)
        default:
            guard let id = item(value) else { return }
            switch action {
            case "downloadPause": pause(id)
            case "downloadResume": resume(id)
            case "downloadCancel": cancel(id)
            case "downloadRetry": retry(id)
            case "downloadRemove": remove(id)
            case "downloadOpen": open(id)
            case "downloadReveal": revealInFinder(id)
            case "downloadKeep": keep(id)
            case "downloadTrash": trash(id)
            case "downloadWithFDM": sendToFDM(id)
            default: break
            }
        }
    }

    private func item(_ value: String) -> UUID? {
        if let index = Int(value), items.indices.contains(index) { return items[index].id }
        return items.first { $0.fileName == value }?.id
    }

    private func applySetting(_ assignment: String) {
        let parts = assignment.split(separator: "=", maxSplits: 1).map(String.init)
        guard parts.count == 2 else { return }
        let on = ["true", "on", "1", "yes"].contains(parts[1].lowercased())
        switch parts[0] {
        case "askWhereToSave": settings.askWhereToSave = on
        case "openSafeFiles": settings.openSafeFiles = on
        case "notify": settings.notifyWhenFinished = on
        case "organize": settings.rules.isEnabled = on
        case "fdmMode": if let mode = FDMMode(rawValue: parts[1]) { settings.fdmMode = mode }
        case "fdmMinimumMB": settings.fdmMinimumMB = Int(parts[1]) ?? 0
        case "fdmCookies": settings.fdmPassCookies = on
        case "fdmContextMenu": settings.fdmContextMenu = on
        default: break
        }
    }

    /// The `downloads` section of `dump`.
    func automationState() -> [String: Any] {
        [
            "folder": settings.folderPath,
            "historyFile": store.fileURL.path,
            "askWhereToSave": settings.askWhereToSave,
            "openSafeFiles": settings.openSafeFiles,
            "organize": settings.rules.isEnabled,
            "notify": settings.notifyWhenFinished,
            "fdmInstalled": fdm.isInstalled,
            "fdmVersion": fdm.version ?? "",
            "fdmMode": settings.fdmMode.rawValue,
            "fdmMinimumMB": settings.fdmMinimumMB,
            "fdmCookies": settings.fdmPassCookies,
            "fdmContextMenu": MosaicCEFDownloads.showsFDMContextMenuItem,
            "lastFDMRoute": lastFDMRoute ?? "",
            "geckoWatching": geckoWatching,
            "activeCount": activeCount,
            "overallProgress": overallProgress ?? -1,
            "popoverShown": isPopoverShown,
            "unseenCompletion": hasUnseenCompletion,
            "pendingPrompt": prompter.pending.map { ["kind": $0.kind, "title": $0.title, "buttons": $0.buttons] } as Any? ?? NSNull(),
            "lastNotification": notifier.lastMessage ?? "",
            "notificationAuthorization": notifier.authorization,
            "lastOpenRequest": lastOpenRequest ?? "",
            "lastAutoOpen": lastAutoOpen ?? "",
            "searchText": searchText,
            "filteredCount": filteredItems.count,
            "items": items.map { record in
                let path = record.destinationPath ?? ""
                let exists = !path.isEmpty && FileManager.default.fileExists(atPath: path)
                return [
                    "fileName": record.fileName, "url": record.url, "referrer": record.referrer ?? "",
                    "engine": record.engine.rawValue, "state": record.state.rawValue,
                    "totalBytes": record.totalBytes, "receivedBytes": record.receivedBytes,
                    "speed": record.speed, "path": path, "fileExists": exists,
                    "quarantine": exists ? (Quarantine.value(URL(fileURLWithPath: path)) ?? "") : "",
                    "error": record.errorMessage ?? "", "canResume": record.canResume,
                    "warning": record.needsSafetyConfirmation ? (record.assessment.summary ?? "") : "",
                    "acknowledged": record.warningAcknowledged,
                    "chromiumID": chromiumID(of: record.id).map(Int.init) ?? -1,
                    "geckoID": geckoID(of: record.id) ?? -1,
                ] as [String: Any]
            },
        ]
    }

    /// Renders the download views offscreen, so they can be checked without the shell
    /// having placed the toolbar button yet. Writes <base>-popover.png, -page.png, -settings.png.
    private func snapshot(to base: String) {
        guard let model else { return }
        let stem = base.hasSuffix(".png") ? String(base.dropLast(4)) : base
        func render<V: View>(_ view: V, size: CGSize, suffix: String) {
            let hosting = NSHostingView(rootView: view.environmentObject(model).frame(width: size.width, height: size.height))
            hosting.frame = CGRect(origin: .zero, size: size)
            hosting.layoutSubtreeIfNeeded()
            guard let rep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else { return }
            hosting.cacheDisplay(in: hosting.bounds, to: rep)
            if let data = rep.representation(using: .png, properties: [:]) {
                try? data.write(to: URL(fileURLWithPath: "\(stem)-\(suffix).png"))
            }
        }
        render(DownloadsPopover(downloads: self).background(Color(nsColor: .windowBackgroundColor)),
               size: CGSize(width: 380, height: 460), suffix: "popover")
        render(DownloadsPage(), size: CGSize(width: 980, height: 640), suffix: "page")
        render(ScrollView { DownloadsSettingsPane() }.background(Color(nsColor: .windowBackgroundColor)),
               size: CGSize(width: 720, height: 1100), suffix: "settings")
        render(HStack { DownloadsToolbarButton() }.padding(8).background(Color(nsColor: .windowBackgroundColor)),
               size: CGSize(width: 60, height: 44), suffix: "button")
    }
}
