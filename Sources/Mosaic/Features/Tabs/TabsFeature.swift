import Combine
import Foundation
import MosaicCore

/// State of the tabs area that is not on `BrowserModel` itself: settings, Recently Closed,
/// the offers the UI shows (switch to an open tab, restore after a crash), and the
/// bookkeeping of sessions and sleeping tabs. Behaviour lives in the `BrowserModel`
/// extensions next to this file; views observe this object for these values.
@MainActor
final class TabsFeature: ObservableObject {
    /// "Switch to the tab that already shows this page?"
    struct DuplicateOffer: Equatable, Identifiable {
        let id = UUID()
        /// The tab that just navigated to the page.
        let tabID: UUID
        /// The tab that already showed it.
        let existingTabID: UUID
        let title: String
        /// Name of the existing tab's workspace when it is not the current one.
        let workspaceName: String?
        /// The new tab was a fresh start-page tab, so switching closes it.
        let closesNewTab: Bool
    }

    @Published var settings: TabSettings {
        didSet {
            guard settings != oldValue else { return }
            AppPaths.defaults.set(settings.encoded(), forKey: Self.settingsKey)
            onSettingsChange?(oldValue)
        }
    }
    /// Newest first. Persisted with the session.
    @Published var recentlyClosed = RecentlyClosedList()
    @Published var duplicateOffer: DuplicateOffer?
    @Published var isTabSearchVisible = false
    /// A session this launch did not restore (start-page setting, or a crash), reopenable
    /// from the History menu and the crash-recovery banner.
    @Published var previousSession: SessionSnapshot?
    /// The previous run crashed and its tabs were not restored: show the banner offering
    /// to restore `previousSession`.
    @Published var offersCrashRecovery = false
    @Published var memoryPressure: MemoryPressure = .normal
    /// The last check by the sleeping-tabs policy, for the Performance page.
    @Published var lastSleepCheck: Date?

    let store = SessionStore(directory: AppPaths.support.appendingPathComponent("Session", isDirectory: true))
    var previousRun: SessionStore.PreviousRun = .firstRun

    // Bookkeeping used by the BrowserModel extensions.
    /// The selected tab of each workspace, so switching back returns to it.
    var selectionByWorkspace: [UUID: UUID] = [:]
    /// While set, closed tabs are collected into one Recently Closed entry (group/workspace).
    var closingBatch: ClosedItem?
    /// ⌘W on a pinned tab: the tab and when, so a second press closes it.
    var pinnedCloseRequest: (tabID: UUID, at: Date)?
    /// Tabs the downloads area reports as downloading; never put to sleep.
    var downloadingTabs: Set<UUID> = []
    /// IDs (in the snapshot) of tabs restored at launch, so a later "restore" skips them.
    var restoredSessionTabIDs: Set<UUID> = []
    /// The last snapshot written (with `savedAt` cleared), to skip identical writes.
    var lastSavedSession: SessionSnapshot?
    var saveFailureReported = false
    var isStarted = false
    var isFinished = false
    var saveTask: Task<Void, Never>?
    var periodicTask: Task<Void, Never>?
    var sleepTask: Task<Void, Never>?
    var isSleepCheckRunning = false
    var memoryPressureSource: DispatchSourceMemoryPressure?
    var cancellables: Set<AnyCancellable> = []
    var onSettingsChange: ((TabSettings) -> Void)?

    static let settingsKey = "tabSettings"

    init() {
        settings = TabSettings.decode(AppPaths.defaults.data(forKey: Self.settingsKey))
    }
}
