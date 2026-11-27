import AppKit
import CEFBridge
import Combine
import CryptoKit
import LocalAuthentication
import MosaicCore

/// Mosaic's password manager: saved passwords in the Keychain, the save prompt after a
/// sign-in, filling on request, and the generator. One instance, owned by `BrowserModel`
/// (`model.passwords`).
///
/// Engines report what pages do through the shared page script (`passwords-page.js`):
/// Chromium through `MosaicCEF.passwordMessageHandler`, Gecko through events of type
/// "passwords" on the agent's queue (`handleGecko`). Both arrive here as `PasswordPageMessage`,
/// with the origin established by the engine side.
@MainActor
@Observable
final class PasswordManager {
    /// What the overlay above the page is asking, per tab.
    enum Prompt: Equatable {
        /// Save (or update) a password just used to sign in or sign up.
        case save(PasswordSubmission, SaveOffer)
        /// A new-password field has focus: offer a generated password.
        case generate(SiteOrigin, String)
        /// Several saved logins for the site: which one to fill.
        case choose(SiteOrigin, [PasswordCredential])

        /// Distinguishes one prompt from the next, for view identity.
        var key: String {
            switch self {
            case .save(let submission, _): "save \(submission.origin) \(submission.username) \(submission.password.hashValue)"
            case .generate(let origin, let password): "generate \(origin) \(password.hashValue)"
            case .choose(let origin, let credentials): "choose \(origin) \(credentials.count)"
            }
        }
    }

    /// What Mosaic knows about the page in a tab.
    struct PageState {
        /// The top-level document's identifier from the page script.
        var document: String?
        var origin: SiteOrigin?
        var hasLogin = false
        var hasSignup = false
        /// A submitted form whose page navigated away; judged when the next page reports.
        var pending: Pending?
        /// The username of step one of a two-step sign-in.
        var identifier: (origin: SiteOrigin, username: String, date: Date)?
    }

    struct Pending {
        let submission: PasswordSubmission
        let document: String?
        let date: Date
    }

    let vault: PasswordVault
    let authenticator: PasswordAuthenticator
    private(set) var prompts: [UUID: Prompt] = [:]
    private(set) var pages: [UUID: PageState] = [:]
    /// Bumped whenever the saved list changes, so views reading `vault` refresh.
    private(set) var revision = 0
    /// For scripted checks: the last fill and the last submission (never the password).
    private(set) var lastFill: (origin: String, count: Int, date: Date)?
    private(set) var lastSubmission: (origin: String, username: String, mode: String, confirmed: Bool)?

    /// Whether a tab is private. Set by the privacy area (`{ $0.isPrivate }`) once tabs have
    /// that property; saving is never offered in a private tab, filling is allowed.
    static var isPrivateTab: (BrowserTab) -> Bool = { _ in false }
    /// Tabs a scripted check marks as private (automation only).
    @ObservationIgnored var simulatedPrivateTabs: Set<UUID> = []

    @ObservationIgnored private(set) weak var model: BrowserModel?
    @ObservationIgnored private var cancellables: Set<AnyCancellable> = []
    @ObservationIgnored let menu = PasswordsMenu()

    init(store: PasswordStore? = nil, preferencesFile: URL? = nil) {
        let store = store ?? KeychainPasswordStore(scope: Self.keychainScope)
        let file = preferencesFile ?? AppPaths.support.appendingPathComponent("Passwords/settings.json")
        vault = PasswordVault(store: store, preferencesStore: PasswordPreferencesStore(fileURL: file))
        authenticator = PasswordAuthenticator()
    }

    /// The Keychain scope of this profile; an isolated test profile gets one of its own.
    static var keychainScope: KeychainPasswordStore.Scope {
        guard AppPaths.isIsolatedProfile else { return .standard }
        let digest = SHA256.hash(data: Data(AppPaths.support.standardizedFileURL.path.utf8))
        return .testProfile(digest.prefix(6).map { String(format: "%02x", $0) }.joined())
    }

    /// Connects to the engines. Called once from `BrowserModel.init`.
    func attach(to model: BrowserModel) {
        self.model = model
        menu.passwords = self
        if let url = AppPaths.geckoAgent?.appendingPathComponent("passwords-page.js"),
           let script = try? String(contentsOf: url, encoding: .utf8)
        {
            MosaicCEF.passwordPageScript = script
            MosaicCEF.passwordLoadScript = PasswordPageScripts.load
        }
        MosaicCEF.passwordMessageHandler = { [weak self] view, origin, frame, json in
            MainActor.assumeIsolated {
                guard var message = PasswordPageMessage.decode(json: json) else { return }
                // The bridge's origin and frame, never the page's.
                message.origin = SiteOrigin(url: origin)
                message.frame = frame == .main ? .main : .sameOrigin
                message.tab = nil
                message.isPrivate = nil
                self?.handleChromium(message, view: view)
            }
        }
        // Gecko: register the window actors whenever the engine (re)starts.
        model.gecko.$status
            .receive(on: DispatchQueue.main)
            .sink { [weak self] status in
                guard status == .running else { return }
                Task { @MainActor in
                    do {
                        try await self?.model?.gecko.call("passwordsStart")
                    } catch {
                        self?.model?.notice = "Password saving is unavailable in Mozilla tabs: \(error.localizedDescription)"
                    }
                }
            }
            .store(in: &cancellables)
    }

    // MARK: - Messages from pages

    func handleGecko(_ message: PasswordPageMessage) {
        guard let id = message.tab, let tab = model?.tabs.first(where: { $0.engine == .gecko && $0.geckoID == id }) else { return }
        handle(message, in: tab)
    }

    private func handleChromium(_ message: PasswordPageMessage, view: MCEFBrowserView) {
        // A Chromium page can learn its own channel token, so it could forge this message and
        // get a fill without the user asking. ⌘\ reaches Mosaic's menu directly in Chromium tabs.
        guard message.kind != .fillShortcut else { return }
        guard let tab = model?.tabs.first(where: { $0.chromiumView === view }) else { return }
        handle(message, in: tab)
    }

    func isPrivate(_ tab: BrowserTab) -> Bool {
        Self.isPrivateTab(tab) || simulatedPrivateTabs.contains(tab.id)
    }

    private func handle(_ message: PasswordPageMessage, in tab: BrowserTab) {
        guard let origin = message.origin, message.frame == .main || message.frame == .sameOrigin else { return }
        prune()
        var page = pages[tab.id] ?? PageState()
        defer { pages[tab.id] = page }

        switch message.kind {
        case .page:
            if message.frame == .main {
                if message.document != page.document {
                    // A new top-level document.
                    page.document = message.document
                    page.origin = origin
                    page.hasLogin = false
                    page.hasSignup = false
                    if case .generate = prompts[tab.id] { prompts[tab.id] = nil }
                    if case .choose = prompts[tab.id] { prompts[tab.id] = nil }
                    if let pending = page.pending, pending.document != message.document {
                        page.pending = nil
                        // Back on a sign-in form of the same site: the sign-in failed.
                        let failed = origin == pending.submission.origin && message.hasLogin == true
                        if !failed { offerToSave(pending.submission, in: tab, isPrivatePage: message.isPrivate == true) }
                    }
                }
                page.hasLogin = message.hasLogin ?? false
                page.hasSignup = message.hasNewPassword ?? false
            } else if origin == page.origin {
                page.hasLogin = page.hasLogin || message.hasLogin == true
                page.hasSignup = page.hasSignup || message.hasNewPassword == true
            }

        case .submit:
            guard var submission = message.submission else { return }
            if submission.username.isEmpty, let step = page.identifier, step.origin == submission.origin,
               Date().timeIntervalSince(step.date) < 600
            {
                submission = PasswordSubmission(origin: submission.origin, username: step.username,
                                                password: submission.password, mode: submission.mode)
            }
            lastSubmission = (submission.origin.string, submission.username, submission.mode.rawValue, message.confirmed == true)
            if message.confirmed == true {
                page.pending = nil
                offerToSave(submission, in: tab, isPrivatePage: message.isPrivate == true)
            } else {
                page.pending = Pending(submission: submission, document: message.document, date: Date())
                // If no page follows (a download, a non-web page), forget it.
                let documentID = message.document, tabID = tab.id
                Task { [weak self] in
                    try? await Task.sleep(nanoseconds: 20_000_000_000)
                    guard let self, var state = self.pages[tabID], state.pending?.document == documentID else { return }
                    state.pending = nil
                    self.pages[tabID] = state
                }
            }

        case .identifier:
            if let username = message.username, !username.isEmpty {
                page.identifier = (origin, username, Date())
            }

        case .generate:
            guard vault.preferences.suggestStrongPasswords, !isPrivate(tab), message.isPrivate != true else { return }
            switch prompts[tab.id] {
            case .save?, .generate?: return
            default: prompts[tab.id] = .generate(origin, PasswordGenerator.generate(vault.preferences.generator))
            }

        case .fillShortcut:
            if tab === model?.selectedTab { fill(in: tab) }

        case .filled:
            lastFill = (origin.string, message.count ?? 0, Date())

        case nil:
            break
        }
    }

    private func offerToSave(_ submission: PasswordSubmission, in tab: BrowserTab, isPrivatePage: Bool) {
        guard let offer = vault.offer(for: submission, isPrivate: isPrivate(tab) || isPrivatePage) else {
            revision += 1
            return
        }
        prompts[tab.id] = .save(submission, offer)
    }

    /// Forgets tabs that have closed.
    private func prune() {
        guard let model else { return }
        let open = Set(model.tabs.map(\.id))
        for id in pages.keys where !open.contains(id) { pages[id] = nil }
        for id in prompts.keys where !open.contains(id) { prompts[id] = nil }
    }

    // MARK: - Prompt answers

    func prompt(for tab: BrowserTab?) -> Prompt? {
        tab.flatMap { prompts[$0.id] }
    }

    func dismissPrompt(for tab: BrowserTab) {
        prompts[tab.id] = nil
    }

    /// Save / Update.
    func acceptSave(in tab: BrowserTab, username: String? = nil) {
        guard case .save(var submission, var offer)? = prompts[tab.id] else { return }
        prompts[tab.id] = nil
        if let username, username != submission.username, case .save = offer {
            submission = PasswordSubmission(origin: submission.origin, username: username,
                                            password: submission.password, mode: submission.mode)
            if vault.credentials.contains(where: { $0.id == CredentialID(origin: submission.origin, username: username) }) {
                offer = .update(CredentialID(origin: submission.origin, username: username))
            }
        }
        do {
            let outcome = try vault.accept(offer, for: submission)
            model?.notice = outcome == .updated ? "Password updated" : "Password saved"
        } catch {
            model?.notice = "Couldn’t save the password: \(error.localizedDescription)"
        }
        revision += 1
    }

    /// Never for This Site.
    func neverSave(in tab: BrowserTab) {
        guard case .save(let submission, _)? = prompts[tab.id] else { return }
        prompts[tab.id] = nil
        vault.setNeverSave(true, for: submission.origin)
        revision += 1
    }

    /// Use Password (generator prompt).
    func acceptGenerated(in tab: BrowserTab) {
        guard case .generate(let origin, let password)? = prompts[tab.id] else { return }
        prompts[tab.id] = nil
        run(.generated(password), in: tab, origin: origin)
    }

    func regenerate(in tab: BrowserTab) {
        guard case .generate(let origin, _)? = prompts[tab.id] else { return }
        prompts[tab.id] = .generate(origin, PasswordGenerator.generate(vault.preferences.generator))
    }

    // MARK: - Filling

    /// The selected tab's site, if it is a web page.
    func origin(of tab: BrowserTab?) -> SiteOrigin? {
        guard let tab, tab.internalPage == nil, !tab.isStartPage else { return nil }
        return SiteOrigin(url: tab.url)
    }

    /// Saved logins that may be filled into `tab` (its exact origin).
    func fillable(in tab: BrowserTab?) -> [PasswordCredential] {
        _ = revision
        guard let origin = origin(of: tab) else { return [] }
        return vault.matches(for: origin)
    }

    /// True when the tab's page shows a sign-in form and there is something to fill.
    func offersFill(in tab: BrowserTab?) -> Bool {
        guard let tab, let page = pages[tab.id], page.hasLogin, page.origin == origin(of: tab) else { return false }
        return !fillable(in: tab).isEmpty
    }

    /// Fills `credential`, or the only saved login for the site, or asks which one.
    /// Only ever on the user's request (key button, ⌘\, menu).
    func fill(in tab: BrowserTab?, credential: PasswordCredential? = nil) {
        guard let tab, let origin = origin(of: tab) else { return }
        let matches = vault.matches(for: origin)
        let chosen = credential ?? (matches.count == 1 ? matches[0] : nil)
        guard let chosen else {
            if matches.isEmpty {
                model?.notice = "No saved passwords for \(origin.displayName)"
            } else {
                prompts[tab.id] = .choose(origin, matches)
            }
            return
        }
        guard chosen.origin == origin else { return }
        if case .choose? = prompts[tab.id] { prompts[tab.id] = nil }
        let password: String
        do {
            password = try vault.password(for: chosen.id)
        } catch {
            model?.notice = "Couldn’t read the password: \(error.localizedDescription)"
            return
        }
        vault.markUsed(chosen.id)
        revision += 1
        run(.credential(chosen.username, password), in: tab, origin: origin)
    }

    private enum Filling {
        case credential(String, String)
        case generated(String)
    }

    private func run(_ filling: Filling, in tab: BrowserTab, origin: SiteOrigin) {
        switch tab.engine {
        case .chromium:
            guard let view = tab.chromiumView else { return }
            let body: String
            switch filling {
            case .credential(let username, let password): body = PasswordPageScripts.fill(username: username, password: password)
            case .generated(let password): body = PasswordPageScripts.fillGenerated(password: password)
            }
            if view.runPasswordScript(body, inFramesWithOrigin: origin.string) == 0 {
                model?.notice = "This page is not on \(origin.displayName)"
            }
        case .gecko:
            guard let id = tab.geckoID, let gecko = model?.gecko else { return }
            Task {
                let count: Int?
                switch filling {
                case .credential(let username, let password):
                    count = try? await gecko.call("passwordsFill", id, origin.string, username, password) as? Int
                case .generated(let password):
                    count = try? await gecko.call("passwordsFillGenerated", id, origin.string, password) as? Int
                }
                lastFill = (origin.string, count ?? 0, Date())
                if (count ?? 0) == 0 { model?.notice = "No sign-in form to fill on this page" }
            }
        }
    }

    /// Edit › Generate Password: offers a generated password for the page's new-password field.
    func offerGeneratedPassword(in tab: BrowserTab?) {
        guard let tab, let origin = origin(of: tab) else { return }
        prompts[tab.id] = .generate(origin, PasswordGenerator.generate(vault.preferences.generator))
    }

    // MARK: - Manager actions (settings page)

    func changed() {
        vault.reload()
        revision += 1
    }

    func openSettings() {
        model?.openSettings(.passwords)
    }

    func showNotice(_ text: String) {
        model?.notice = text
    }
}

/// Asks for Touch ID or the Mac's login password before passwords are shown, copied,
/// edited or exported.
@MainActor
final class PasswordAuthenticator {
    /// After a successful check, passwords stay viewable this long (or until `lock`).
    static let unlockDuration: TimeInterval = 5 * 60
    private var unlockedUntil: Date?

    /// Scripted checks on an isolated profile cannot answer a system dialog, and must not put
    /// one on the screen of the person using the Mac. They only ever see that profile's own
    /// Keychain scope (see `PasswordManager.keychainScope`), never the real passwords.
    var bypassesForAutomation: Bool {
        AppPaths.isIsolatedProfile && CommandLine.arguments.contains(Automation.flag)
    }

    var isUnlocked: Bool { (unlockedUntil ?? .distantPast) > Date() }

    /// `fresh` ignores an earlier unlock (export always asks again).
    func authenticate(reason: String, fresh: Bool = false) async -> Bool {
        if bypassesForAutomation { return true }
        if !fresh, isUnlocked { return true }
        let context = LAContext()
        context.localizedCancelTitle = "Cancel"
        var error: NSError?
        // Touch ID or Apple Watch where available, the login password otherwise (or as fallback).
        guard context.canEvaluatePolicy(.deviceOwnerAuthentication, error: &error) else { return false }
        do {
            guard try await context.evaluatePolicy(.deviceOwnerAuthentication, localizedReason: reason) else { return false }
            unlockedUntil = Date().addingTimeInterval(Self.unlockDuration)
            return true
        } catch {
            return false
        }
    }

    func lock() {
        unlockedUntil = nil
    }
}

/// Copies secrets to the clipboard marked as concealed (so clipboard managers skip them),
/// and clears them after a minute if nothing else was copied meanwhile.
enum PasswordClipboard {
    static func copy(_ text: String, secret: Bool) {
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(text, forType: .string)
        guard secret else { return }
        board.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        let change = board.changeCount
        DispatchQueue.main.asyncAfter(deadline: .now() + 60) {
            if board.changeCount == change { board.clearContents() }
        }
    }
}
