import AppKit
import CEFBridge
import MosaicCore

/// The platform area's services: the Gecko runtime installer, per-site engine rules,
/// web-content languages, Mosaic's update check and safe engine switching.
/// Owned by `BrowserModel.platform`; wired up by `attach(to:)` during the model's init.
@MainActor
final class PlatformServices {
    let runtime = GeckoRuntimeInstaller()
    let rules = EngineRuleStore()
    let languages = LanguageSettings()
    let updates = UpdateChecker()
    let switches = EngineSwitchTracker()

    private(set) weak var model: BrowserModel?

    func attach(to model: BrowserModel) {
        self.model = model
        runtime.model = model
        languages.model = model
        updates.runtime = runtime

        // Languages go into Gecko each time it starts, before its first page loads.
        let previous = model.gecko.onStarted
        model.gecko.onStarted = { [weak self] in
            await previous?()
            await self?.languages.applyToGecko()
        }
        // Chromium's request context is ready only once its message loop runs.
        languages.applyToChromiumWhenReady()

        // A Mac without any Gecko runtime (the downloadable app on first launch): start in
        // Chromium so browsing works at once, unless the person already chose an engine.
        // Mozilla tabs, and Settings › Engine, offer to install the runtime.
        if model.gecko.runtime == nil, model.chromiumAvailable,
           AppPaths.defaults.string(forKey: "engine") == nil
        {
            model.platformFollowEngine(.chromium, remember: false)
        }

        updates.startSchedule()
    }
}

// MARK: - Per-site engine rules

/// Persisted per-site engine rules (Settings › Engine).
@MainActor
final class EngineRuleStore: ObservableObject {
    @Published private(set) var ruleSet: EngineRuleSet

    private static let key = "engineRules"

    init() {
        if let data = AppPaths.defaults.data(forKey: Self.key),
           let saved = try? JSONDecoder().decode(EngineRuleSet.self, from: data)
        {
            ruleSet = saved
        } else {
            ruleSet = EngineRuleSet()
        }
    }

    var rules: [EngineRule] { ruleSet.rules }

    func engine(for url: URL) -> EngineKind? {
        ruleSet.engine(for: url).flatMap(EngineKind.init(rawValue:))
    }

    func rule(for url: URL) -> EngineRule? { ruleSet.rule(for: url) }

    /// Adds or replaces the rule for `domain`. Returns false if `domain` is not a domain.
    @discardableResult
    func set(domain: String, engine: EngineKind, includesSubdomains: Bool = true, id: UUID = UUID()) -> Bool {
        var updated = ruleSet
        guard updated.upsert(EngineRule(id: id, domain: domain, engine: engine.rawValue,
                                        includesSubdomains: includesSubdomains))
        else { return false }
        save(updated)
        return true
    }

    func update(_ rule: EngineRule) {
        var updated = ruleSet
        if updated.upsert(rule) { save(updated) }
    }

    func remove(_ rule: EngineRule) {
        var updated = ruleSet
        updated.remove(id: rule.id)
        save(updated)
    }

    private func save(_ updated: EngineRuleSet) {
        ruleSet = updated
        if let data = try? JSONEncoder().encode(updated) { AppPaths.defaults.set(data, forKey: Self.key) }
    }
}

// MARK: - Languages

/// The languages web pages are asked for, applied to both engines (Settings › Languages).
@MainActor
final class LanguageSettings: ObservableObject {
    @Published private(set) var preferences: LanguagePreferences
    /// False while the list simply follows macOS's preferred languages.
    @Published private(set) var isCustom: Bool
    /// Why the last attempt to apply the list to an engine failed, if it did.
    @Published private(set) var chromiumError: String?
    @Published private(set) var geckoError: String?

    weak var model: BrowserModel?

    private static let key = "contentLanguages"

    init() {
        if let saved = AppPaths.defaults.stringArray(forKey: Self.key), !saved.isEmpty {
            preferences = LanguagePreferences(tags: saved)
            isCustom = true
        } else {
            preferences = LanguagePreferences.fromSystem(Locale.preferredLanguages)
            isCustom = false
        }
    }

    func edit(_ change: (inout LanguagePreferences) -> Void) {
        var updated = preferences
        change(&updated)
        guard updated != preferences, !updated.tags.isEmpty else { return }
        preferences = updated
        isCustom = true
        AppPaths.defaults.set(updated.tags, forKey: Self.key)
        apply()
    }

    func useSystemLanguages() {
        AppPaths.defaults.removeObject(forKey: Self.key)
        preferences = LanguagePreferences.fromSystem(Locale.preferredLanguages)
        isCustom = false
        apply()
    }

    func apply() {
        applyToChromium()
        Task { await applyToGecko() }
    }

    func applyToChromium() {
        guard model?.chromiumAvailable == true else { return }
        chromiumError = MosaicCEF.setAcceptLanguageList(preferences.preferenceValue)
    }

    func applyToChromiumWhenReady(attempt: Int = 0) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.applyToChromium()
                if self.chromiumError != nil, attempt < 20 { self.applyToChromiumWhenReady(attempt: attempt + 1) }
            }
        }
    }

    func applyToGecko() async {
        guard let gecko = model?.gecko, gecko.isRunning else { return }
        do {
            try await gecko.call("setAcceptLanguages", preferences.preferenceValue)
            geckoError = nil
        } catch {
            geckoError = error.localizedDescription
        }
    }
}

// MARK: - Mosaic updates

/// Checks GitHub for a newer Mosaic release: on request, or daily if the person chose so.
/// A daily check also asks Mozilla whether a newer Firefox ESR exists for the private runtime.
@MainActor
final class UpdateChecker: ObservableObject {
    @Published var frequency: UpdateCheckFrequency {
        didSet {
            AppPaths.defaults.set(frequency.rawValue, forKey: Keys.frequency)
            runIfDue()
        }
    }
    @Published private(set) var isChecking = false
    @Published private(set) var outcome: ReleaseCheck.Outcome?
    @Published private(set) var lastCheck: Date?

    weak var runtime: GeckoRuntimeInstaller?
    private var timer: Timer?

    private enum Keys {
        static let frequency = "updateCheckFrequency"
        static let lastCheck = "updateLastCheck"
    }

    init() {
        let defaults = AppPaths.defaults
        frequency = defaults.string(forKey: Keys.frequency).flatMap(UpdateCheckFrequency.init(rawValue:)) ?? .manual
        lastCheck = defaults.object(forKey: Keys.lastCheck) as? Date
    }

    /// Mosaic's version and build number, from its Info.plist.
    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    static var currentBuild: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
    }

    func startSchedule() {
        timer?.invalidate()
        // Hourly look at whether a daily check is due; nothing is fetched unless it is.
        timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.runIfDue() }
        }
        Task {
            try? await Task.sleep(nanoseconds: 30_000_000_000)  // let the browser settle first
            runIfDue()
        }
    }

    private func runIfDue() {
        guard frequency.isDue(lastCheck: lastCheck, now: Date()), !isChecking else { return }
        Task {
            await checkNow()
            await runtime?.checkForUpdate()
        }
    }

    func checkNow() async {
        guard !isChecking else { return }
        isChecking = true
        defer { isChecking = false }
        var request = URLRequest(url: ReleaseCheck.latestReleaseURL)
        request.timeoutInterval = 20
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            outcome = ReleaseCheck.outcome(status: status, body: data, current: SoftwareVersion(Self.currentVersion))
        } catch {
            outcome = .failed(error.localizedDescription)
        }
        lastCheck = Date()
        AppPaths.defaults.set(lastCheck, forKey: Keys.lastCheck)
    }
}
