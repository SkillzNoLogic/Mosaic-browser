import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

// Privacy area: private tabs, the connection-security indicator, and the
// automation commands and `dump` fields that exercise the privacy features.

/// How the page in a tab reached Mosaic, for the address bar's padlock.
enum ConnectionSecurity: String {
    /// HTTPS.
    case secure
    /// Plain HTTP from the internet: anyone on the network can read or change it.
    case insecure
    /// Plain HTTP to this Mac or the local network (localhost, 192.168.x.x, .local).
    case local
    /// Not a web page: the start page, Mosaic's own pages, `about:`, files.
    case notApplicable

    var symbol: String? {
        switch self {
        case .secure: "lock.fill"
        case .insecure: "lock.open.trianglebadge.exclamationmark"
        case .local: "lock.open"
        case .notApplicable: nil
        }
    }

    var label: String {
        switch self {
        case .secure: "Secure connection"
        case .insecure: "Not secure: this page was loaded over plain HTTP"
        case .local: "Local network page (not encrypted)"
        case .notApplicable: ""
        }
    }
}

extension BrowserTab {
    /// The data point the shell draws as the padlock next to the address.
    var connectionSecurity: ConnectionSecurity {
        guard internalPage == nil, let components = URLComponents(string: url),
              let scheme = components.scheme?.lowercased()
        else { return .notApplicable }
        switch scheme {
        case "https", "wss": return .secure
        case "http", "ws":
            return NavigationPolicy.isLocal(host: components.host ?? "") ? .local : .insecure
        default: return .notApplicable
        }
    }
}

extension BrowserModel {
    /// Opens a private tab: Chromium, with an in-memory profile shared only by
    /// private tabs and discarded when the last one closes. Nothing it does is
    /// written to disk, and it never appears in history (`BrowserTab.isPrivate`).
    /// Returns nil (with a notice) when this build has no Chromium engine.
    @discardableResult
    func newPrivateTab(url: String = "") -> BrowserTab? {
        guard chromiumAvailable else {
            notice = "Private tabs need the Chromium engine, which is not included in this build."
            return nil
        }
        let tab = newTab(url: "", engine: .chromium, select: true)
        tab.isPrivate = true
        if !url.isEmpty { load(url, in: tab) }
        return tab
    }

    var privateTabs: [BrowserTab] { tabs.filter(\.isPrivate) }


    /// Opens Settings › Privacy & Security with the Clear Browsing Data sheet.
    func showClearBrowsingData() {
        openSettings(.privacy)
        privacy.showClearDataSheet = true
    }

    // MARK: Automation

    /// Commands named `privacy.*` (see Automation.swift):
    ///
    ///     privacy.set            value "httpsOnly=on", "strip=off", "thirdPartyCookies=on",
    ///                            "clearOnExit=on", "cosmetic=off", "doh=strict:custom:https://…",
    ///                            "doh=automatic:quad9", "doh=off", "list=<id>:on"
    ///     privacy.filters        value: My filters text (\n for new lines)
    ///     privacy.validate       value: filters, out: JSON file
    ///     privacy.newPrivateTab  value: URL (optional)
    ///     privacy.blocked        value: JSON file (blocked requests of the selected tab)
    ///     privacy.siteData       value: JSON file
    ///     privacy.clearSite      value: site, out: file written when done
    ///     privacy.clearAllSiteData  out: file written when done
    ///     privacy.clear          value "history,cookiesAndSiteData,cache:allTime", out: file
    ///     privacy.cookies        value: JSON file (Chromium cookies, default and private profile)
    ///     privacy.status         value: JSON file (the `privacy` dump plus engine details)
    ///     privacy.settings       opens Settings › Privacy & Security; value "clear" adds the sheet
    func handlePrivacyAutomation(_ action: String, _ command: [String: Any]) {
        let value = command["value"] as? String ?? ""
        let out = command["out"] as? String
        func write(_ object: Any, to path: String?) { PrivacyAutomationOutput.write(object, to: path) }
        switch action {
        case "privacy.set":
            applyPrivacySetting(value)
        case "privacy.filters":
            try? blocker.saveUserFilters(value.replacingOccurrences(of: "\\n", with: "\n"))
        case "privacy.validate":
            let checks = blocker.validate(filters: value.replacingOccurrences(of: "\\n", with: "\n"))
            write(checks.map { ["line": $0.line, "text": $0.text, "kind": $0.kind.rawValue, "message": $0.message] },
                  to: out)
        case "privacy.newPrivateTab":
            newPrivateTab(url: value)
        case "privacy.blocked":
            guard let tab = selectedTab else { return }
            Task {
                let requests = await blocker.blockedRequests(in: tab)
                write(requests.map {
                    ["url": $0.url, "type": $0.type, "count": $0.count, "filter": $0.filter, "list": $0.list ?? ""]
                }, to: value)
            }
        case "privacy.siteData":
            Task {
                await privacy.loadSiteData()
                write(privacy.siteData.map {
                    ["site": $0.site, "hosts": $0.hosts.sorted(), "chromiumCookies": $0.chromiumCookies,
                     "geckoCookies": $0.geckoCookies, "geckoStorageBytes": $0.geckoStorageBytes] as [String: Any]
                }, to: value)
            }
        case "privacy.clearSite":
            Task {
                await privacy.loadSiteData()
                let entry = privacy.siteData.first { $0.site == value } ?? SiteDataEntry(site: value)
                await privacy.clearSiteData(entry)
                write(["cleared": value], to: out)
            }
        case "privacy.clearAllSiteData":
            Task {
                await privacy.clearAllSiteData()
                write(["cleared": "all"], to: out)
            }
        case "privacy.clear":
            let parts = value.split(separator: ":").map(String.init)
            let kinds = Set((parts.first ?? "").split(separator: ",").compactMap { BrowsingDataKind(rawValue: String($0)) })
            let range = parts.count > 1 ? BrowsingDataTimeRange(rawValue: parts[1]) ?? .allTime : .allTime
            Task {
                let summary = await privacy.clearBrowsingData(kinds, range: range)
                write(["summary": summary], to: out)
            }
        case "privacy.cookies":
            MosaicPrivacyCEF.visitCookies { cookies in
                MosaicPrivacyCEF.visitPrivateCookies { privateCookies in
                    PrivacyAutomationOutput.write(["default": cookies, "private": privateCookies,
                           "privateBrowsers": MosaicPrivacyCEF.privateBrowserCount,
                           "hasPrivateProfile": MosaicPrivacyCEF.hasPrivateProfile] as [String: Any], to: value)
                }
            }
        case "privacy.status":
            var status = privacyDump()
            status["chromiumPreferences"] = privacy.chromiumPreferenceValues()
            status["chromiumPreferenceErrors"] = privacy.chromiumPreferenceErrors
            Task {
                if gecko.isRunning, let gecko = try? await gecko.call("privacyStatus") {
                    status["gecko"] = gecko
                }
                write(status, to: value)
            }
        case "privacy.previewPane":
            // Automation only: renders Settings › Privacy & Security offscreen to a PNG,
            // for checking the page before the shell hosts settings. Never shown on screen.
            previewPrivacyPane(to: value, height: Double(command["out"] as? String ?? "") ?? 2600)
        case "privacy.settings":
            if value == "clear" { showClearBrowsingData() } else { openSettings(.privacy) }
        default:
            break
        }
    }

    private func previewPrivacyPane(to path: String, height: Double) {
        let size = NSSize(width: 720, height: height)
        let window = NSWindow(contentRect: NSRect(origin: NSPoint(x: -20_000, y: -20_000), size: size),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: PrivacySettingsPane().environmentObject(self).frame(width: size.width, height: size.height))
        host.frame = NSRect(origin: .zero, size: size)
        window.contentView = host
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            host.layoutSubtreeIfNeeded()
            if let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds) {
                host.cacheDisplay(in: host.bounds, to: bitmap)
                try? bitmap.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
            }
            window.contentView = nil
            window.close()
        }
    }

    private func applyPrivacySetting(_ assignment: String) {
        let parts = assignment.split(separator: "=", maxSplits: 1).map(String.init)
        guard parts.count == 2 else { return }
        let key = parts[0], value = parts[1]
        let on = value == "on" || value == "true"
        switch key {
        case "httpsOnly": privacy.update { $0.httpsOnly = on }
        case "strip": privacy.update { $0.stripTrackingParameters = on }
        case "thirdPartyCookies": privacy.update { $0.blockThirdPartyCookies = on }
        case "clearOnExit": privacy.update { $0.clearCookiesOnExit = on }
        case "httpException":
            privacy.update { $0.setHTTPException(!value.hasPrefix("-"), site: value.trimmingCharacters(in: ["-"])) }
        case "cosmetic": blocker.setCosmeticEnabled(on)
        case "doh":
            let pieces = value.split(separator: ":", maxSplits: 2).map(String.init)
            let mode = DNSOverHTTPS.Mode(rawValue: pieces[0]) ?? .off
            privacy.update {
                $0.dnsOverHTTPS.mode = mode
                if pieces.count > 1 { $0.dnsOverHTTPS.providerID = pieces[1] }
                if pieces.count > 2 { $0.dnsOverHTTPS.customTemplate = pieces[2] }
            }
        case "list":
            let pieces = value.split(separator: ":").map(String.init)
            if let list = FilterListCatalog.all.first(where: { $0.id == pieces.first }) {
                blocker.setList(list, enabled: pieces.last == "on")
            }
        default: break
        }
    }

    /// The `privacy` field of Automation's `dump`.
    func privacyDump() -> [String: Any] {
        let settings = privacy.settings
        return [
            "settings": [
                "httpsOnly": settings.httpsOnly,
                "httpsExceptions": settings.httpsExceptions,
                "sessionHTTPExceptions": privacy.sessionHTTPExceptions.sorted(),
                "stripTrackingParameters": settings.stripTrackingParameters,
                "blockThirdPartyCookies": settings.blockThirdPartyCookies,
                "clearCookiesOnExit": settings.clearCookiesOnExit,
                "dohMode": settings.dnsOverHTTPS.mode.rawValue,
                "dohTemplate": settings.dnsOverHTTPS.template ?? "",
            ] as [String: Any],
            "tabs": tabs.map { tab in
                ["isPrivate": tab.isPrivate, "security": tab.connectionSecurity.rawValue, "url": tab.url] as [String: Any]
            },
            "privateBrowsers": MosaicPrivacyCEF.privateBrowserCount,
            "hasPrivateProfile": MosaicPrivacyCEF.hasPrivateProfile,
            "blocker": [
                "cosmeticEnabled": blocker.cosmeticEnabled,
                "cosmeticGeneric": blocker.cosmeticStats.generic,
                "cosmeticSpecific": blocker.cosmeticStats.specific,
                "cosmeticUnsupported": blocker.cosmeticStats.unsupported,
                "lists": blocker.enabledListIDs.sorted(),
                "userFilterLines": blocker.userFilters.split(whereSeparator: \.isNewline).count,
                "lastUpdate": blocker.lastUpdateMessage ?? "",
            ] as [String: Any],
            "lastClear": privacy.lastClearSummary ?? "",
        ]
    }
}

/// Writes automation results as JSON (from any thread).
private enum PrivacyAutomationOutput {
    static func write(_ object: Any, to path: String?) {
        guard let path else { return }
        let data = (try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]))
            ?? Data("\(object)".utf8)
        try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
    }
}
