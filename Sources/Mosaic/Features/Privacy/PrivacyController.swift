import AppKit
import CEFBridge
import MosaicCore

/// Privacy and network security across both engines: HTTPS-only mode,
/// tracking-parameter removal, third-party cookies, DNS over HTTPS, cookies and
/// site data, clearing browsing data, and the private (in-memory) profile.
///
/// - Chromium: navigation rewriting in the bridge (`MosaicPrivacyCEF`),
///   request-context and browser-wide preferences, `CefCookieManager`, DevTools
///   `Storage.clearDataForOrigin`.
/// - Gecko: preferences and `Services.clearData` through the agent
///   (`configurePrivacy`, `siteData`, `clearSiteData`, `clearBrowsingData`).
@MainActor
final class PrivacyController: ObservableObject {
    @Published private(set) var settings: PrivacySettings
    /// Hosts the user let through over plain HTTP this session (HTTPS-only page).
    @Published private(set) var sessionHTTPExceptions: Set<String> = []
    @Published private(set) var siteData: [SiteDataEntry] = []
    @Published private(set) var isLoadingSiteData = false
    /// Whether the last site-data listing could include Gecko's data.
    @Published private(set) var siteDataIncludesGecko = false
    /// Chromium preferences that could not be applied: name -> reason.
    @Published private(set) var chromiumPreferenceErrors: [String: String] = [:]
    /// Gecko's privacy preferences as last reported by the agent (JSON).
    @Published private(set) var geckoStatus: String = ""
    /// Shown over Settings › Privacy & Security; set by the menu command.
    @Published var showClearDataSheet = false
    /// The last clearing's outcome, for the settings page.
    @Published private(set) var lastClearSummary: String?

    /// Remembered site permissions (engine area owns the store; this page edits it).
    let sitePermissions: SitePermissionStore

    private unowned let gecko: GeckoEngine
    private unowned let blocker: ContentBlocker
    /// Gecko data to clear the next time it starts (it was not running when asked).
    private var pendingGeckoClear: (kinds: Set<BrowsingDataKind>, since: Date?)?
    private var suffixList: PublicSuffixList?
    /// Addresses of open non-private tabs (their origins carry ports cookies do not).
    private var openPages: () -> [String] = { [] }

    static let geckoPrivateNotice =
        "Private tabs run on Chromium. Mozilla private windows are not supported in Mosaic yet."

    private enum Keys {
        static let settings = "privacySettings"
    }

    /// `sitePermissions` is the engine area's store (one instance for the whole app, so what
    /// the settings page changes is what the engines' prompts consult).
    init(gecko: GeckoEngine, blocker: ContentBlocker, sitePermissions: SitePermissionStore) {
        self.gecko = gecko
        self.blocker = blocker
        if let data = AppPaths.defaults.data(forKey: Keys.settings),
           let saved = try? JSONDecoder().decode(PrivacySettings.self, from: data) {
            settings = saved
        } else {
            settings = PrivacySettings()
        }
        self.sitePermissions = sitePermissions
    }

    // MARK: - Lifecycle

    /// Installs the Chromium hooks and applies the settings. Call once at launch.
    /// - Parameter openPages: the addresses of open non-private tabs.
    func start(openPages: @escaping () -> [String] = { [] }) {
        self.openPages = openPages
        MosaicPrivacyCEF.navigationRewriter = { [weak self] url, _, allowInsecure in
            MainActor.assumeIsolated { self?.rewrite(url, allowInsecure: allowInsecure) }
        }
        MosaicPrivacyCEF.insecureContinueHandler = { [weak self] host in
            MainActor.assumeIsolated { self?.allowHTTPThisSession(host: host) }
        }
        applyChromium()
        if settings.clearCookiesOnExit {
            // Backstop for "clear on exit": whatever Chromium kept from the last
            // session goes before any page loads.
            Task { await clearChromiumSiteData(since: nil) }
        }
    }

    /// Called each time Gecko has started (before any Mosaic tab loads there).
    func configureGecko() async {
        guard gecko.isRunning else { return }
        let options: [String: Any] = [
            "httpsOnly": settings.httpsOnly,
            "httpsExceptions": settings.httpsExceptions + sessionHTTPExceptions.sorted(),
            "stripTracking": settings.stripTrackingParameters,
            "stripList": TrackingParameters.allNames,
            "blockThirdPartyCookies": settings.blockThirdPartyCookies,
            "clearOnExit": settings.clearCookiesOnExit,
            "dnsPreferences": settings.dnsOverHTTPS.geckoPreferences,
            "cosmetic": blocker.cosmeticEnabled,
        ]
        do {
            if let status = try await gecko.call("configurePrivacy", options) {
                geckoStatus = Self.json(status)
            }
            if settings.clearCookiesOnExit {
                try await gecko.call("clearBrowsingData", ["cookiesAndSiteData"], 0)
            }
            if let pending = pendingGeckoClear {
                pendingGeckoClear = nil
                try await gecko.call("clearBrowsingData", pending.kinds.map(\.rawValue),
                                     pending.since.map { Int($0.timeIntervalSince1970 * 1000) } ?? 0)
            }
        } catch {
            NSLog("Mosaic: could not apply privacy settings to Gecko: %@", error.localizedDescription)
        }
    }

    private static func json(_ value: Any) -> String {
        guard JSONSerialization.isValidJSONObject(value),
              let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
        else { return "\(value)" }
        return String(data: data, encoding: .utf8) ?? ""
    }

    // MARK: - Settings

    /// Changes settings, saves them and applies them to both engines.
    func update(_ change: (inout PrivacySettings) -> Void) {
        var next = settings
        change(&next)
        guard next != settings else { return }
        settings = next
        if let data = try? JSONEncoder().encode(settings) {
            AppPaths.defaults.set(data, forKey: Keys.settings)
        }
        applyChromium()
        Task { await configureGecko() }
    }

    private func applyChromium() {
        guard MosaicCEF.isRunning else { return }
        let block = settings.blockThirdPartyCookies
        var errors: [String: String] = [:]
        let profile = MosaicPrivacyCEF.setProfilePreferences([
            // 1 = block third-party cookies (0 = allow).
            "profile.cookie_controls_mode": NSNumber(value: block ? 1 : 0),
        ])
        let global = MosaicPrivacyCEF.setGlobalPreferences(
            settings.dnsOverHTTPS.chromiumPreferences.mapValues { $0 as AnyObject })
        for (name, error) in profile.merging(global, uniquingKeysWith: { $1 }) where !error.isEmpty {
            errors[name] = error
        }
        MosaicPrivacyCEF.setCookiesSessionOnly(settings.clearCookiesOnExit)
        chromiumPreferenceErrors = errors
    }

    /// Chromium's view of the preferences Mosaic sets, for checks.
    func chromiumPreferenceValues() -> [String: Any] {
        var values: [String: Any] = [:]
        for name in ["profile.cookie_controls_mode", "profile.block_third_party_cookies"] {
            values[name] = MosaicPrivacyCEF.profilePreference(name) ?? NSNull()
        }
        for name in ["dns_over_https.mode", "dns_over_https.templates"] {
            values[name] = MosaicPrivacyCEF.globalPreference(name) ?? NSNull()
        }
        return values
    }

    // MARK: - Navigation (HTTPS-only, tracking parameters)

    /// What Chromium should load instead of `url`, if anything.
    func rewrite(_ url: String, allowInsecure: Bool) -> String? {
        NavigationPolicy.rewrite(
            url, settings: settings, sessionExceptions: sessionHTTPExceptions, allowInsecure: allowInsecure)
    }

    func allowHTTPThisSession(host: String) {
        guard let site = BlockerSettings.normalize(host) else { return }
        sessionHTTPExceptions.insert(site)
        Task { await configureGecko() }
    }

    // MARK: - Cookies and site data

    /// Lists every site with cookies or storage in either engine.
    func loadSiteData() async {
        isLoadingSiteData = true
        defer { isLoadingSiteData = false }
        if suffixList == nil {
            let text = try? String(contentsOf: blocker.store.suffixListURL, encoding: .utf8)
            suffixList = text.map(PublicSuffixList.init(text:))
        }
        var records: [SiteDataSummary.Record] = []
        let cookies = await Self.chromiumCookies()
        for cookie in cookies {
            records.append(.init(engine: .chromium, host: cookie["domain"] as? String ?? "", cookies: 1))
        }
        siteDataIncludesGecko = gecko.isRunning
        if gecko.isRunning, let hosts = try? await gecko.call("siteData") as? [[String: Any]] {
            for host in hosts {
                records.append(.init(
                    engine: .gecko, host: host["host"] as? String ?? "",
                    cookies: (host["cookies"] as? Int) ?? 0,
                    storageBytes: Int64((host["bytes"] as? NSNumber)?.int64Value ?? 0)))
            }
        }
        let suffixList = self.suffixList
        siteData = await Task.detached { SiteDataSummary.merge(records, suffixList: suffixList) }.value
    }

    private static func chromiumCookies() async -> [[String: Any]] {
        guard MosaicCEF.isRunning else { return [] }
        return await withCheckedContinuation { continuation in
            MosaicPrivacyCEF.visitCookies { continuation.resume(returning: $0) }
        }
    }

    /// Deletes one site's cookies, storage and (Gecko) cache in both engines.
    func clearSiteData(_ entry: SiteDataEntry) async {
        if MosaicCEF.isRunning {
            _ = await withCheckedContinuation { continuation in
                MosaicPrivacyCEF.deleteCookies(forDomain: entry.site, since: nil) { continuation.resume(returning: $0) }
            }
            let unique = Array(Set(entry.chromiumOrigins + openOrigins(onSite: entry.site))).sorted()
            _ = await withCheckedContinuation { continuation in
                MosaicPrivacyCEF.clearStorage(forOrigins: unique) { continuation.resume(returning: $0) }
            }
        }
        if gecko.isRunning {
            _ = try? await gecko.call("clearSiteData", entry.site)
        }
        await loadSiteData()
    }

    func clearAllSiteData() async {
        await clearChromiumSiteData(since: nil)
        if gecko.isRunning {
            _ = try? await gecko.call("clearBrowsingData", ["cookiesAndSiteData"], 0)
        } else {
            pendingGeckoClear = ([.cookiesAndSiteData], nil)
        }
        await loadSiteData()
    }

    /// Chromium's cookies (created since `since`) and, for a full clear, the
    /// storage of every site that had cookies.
    private func clearChromiumSiteData(since: Date?) async {
        guard MosaicCEF.isRunning else { return }
        if since == nil {
            let cookies = await Self.chromiumCookies()
            let records = cookies.map { SiteDataSummary.Record(engine: .chromium, host: $0["domain"] as? String ?? "", cookies: 1) }
            let origins = Array(Set(SiteDataSummary.merge(records, suffixList: suffixList).flatMap(\.chromiumOrigins)
                + openOrigins(onSite: nil))).sorted()
            if !origins.isEmpty {
                _ = await withCheckedContinuation { continuation in
                    MosaicPrivacyCEF.clearStorage(forOrigins: origins) { continuation.resume(returning: $0) }
                }
            }
        }
        _ = await withCheckedContinuation { continuation in
            MosaicPrivacyCEF.deleteCookies(forDomain: nil, since: since) { continuation.resume(returning: $0) }
        }
    }

    /// Origins (with ports) of open non-private web pages, on `site` or all.
    private func openOrigins(onSite site: String?) -> [String] {
        openPages().compactMap { page in
            guard let components = URLComponents(string: page), let scheme = components.scheme,
                  scheme == "http" || scheme == "https", let host = components.host?.lowercased()
            else { return nil }
            if let site, host != site, !host.hasSuffix("." + site) { return nil }
            return "\(scheme)://\(host)" + (components.port.map { ":\($0)" } ?? "")
        }
    }

    // MARK: - Clear browsing data

    /// Clears `kinds` from both engines and from every registered Mosaic service
    /// (`BrowsingDataRegistry`: history, downloads list). Returns a summary.
    @discardableResult
    func clearBrowsingData(_ kinds: Set<BrowsingDataKind>, range: BrowsingDataTimeRange) async -> String {
        let since = range.startDate()
        var cleared: [String] = []
        if MosaicCEF.isRunning {
            if kinds.contains(.cookiesAndSiteData) {
                await clearChromiumSiteData(since: since)
            }
            if kinds.contains(.cache) {
                await withCheckedContinuation { continuation in
                    MosaicPrivacyCEF.clearCache { continuation.resume() }
                }
            }
            if !kinds.isDisjoint(with: [.cookiesAndSiteData, .cache]) { cleared.append("Chromium") }
        }
        if gecko.isRunning {
            do {
                try await gecko.call("clearBrowsingData", kinds.map(\.rawValue),
                                     since.map { Int($0.timeIntervalSince1970 * 1000) } ?? 0)
                cleared.append("Mozilla")
            } catch {
                NSLog("Mosaic: Gecko could not clear browsing data: %@", error.localizedDescription)
            }
        } else {
            pendingGeckoClear = (kinds, since)
            cleared.append("Mozilla (when it next starts)")
        }
        cleared += await BrowsingDataRegistry.shared.clear(kinds, since: since)
        let summary = "Cleared \(kinds.count == 1 ? kinds.first!.title.lowercased() : "\(kinds.count) kinds of data")"
            + " (\(range.title.lowercased())) from " + cleared.joined(separator: ", ") + "."
        lastClearSummary = summary
        if kinds.contains(.cookiesAndSiteData), !siteData.isEmpty { await loadSiteData() }
        return summary
    }
}
