import Foundation

/// How hard macOS says memory is to come by (`DispatchSource.makeMemoryPressureSource`).
public enum MemoryPressure: String, Sendable, CaseIterable {
    case normal, warning, critical
}

/// What the sleeping-tabs policy needs to know about one tab.
public struct SleepCandidate: Equatable, Sendable {
    public var id: UUID
    public var isSelected: Bool
    public var isAsleep: Bool
    /// False for tabs that hold nothing in an engine (start page, Mosaic's own pages,
    /// tabs whose page was never loaded).
    public var hasLoadedContent: Bool
    public var isPinned: Bool
    public var isPlayingAudio: Bool
    public var hasActiveDownload: Bool
    /// When the tab was last the selected tab.
    public var lastActive: Date

    public init(
        id: UUID, isSelected: Bool = false, isAsleep: Bool = false, hasLoadedContent: Bool = true,
        isPinned: Bool = false, isPlayingAudio: Bool = false, hasActiveDownload: Bool = false, lastActive: Date
    ) {
        self.id = id
        self.isSelected = isSelected
        self.isAsleep = isAsleep
        self.hasLoadedContent = hasLoadedContent
        self.isPinned = isPinned
        self.isPlayingAudio = isPlayingAudio
        self.hasActiveDownload = hasActiveDownload
        self.lastActive = lastActive
    }
}

/// Which background tabs to unload.
///
/// - normal: tabs inactive for the configured minutes (if unloading is on);
/// - warning: tabs inactive for a quarter of that (at least one minute);
/// - critical: every eligible background tab.
/// The two pressure levels apply only with `respondToMemoryPressure`, and work even when
/// timed unloading is off. Exempt in every case: the selected tab, tabs playing audio,
/// tabs with an active download, pinned tabs if so configured, and tabs that hold nothing.
public enum SleepPolicy {
    public enum Exemption: String, Sendable {
        case selected, alreadyAsleep, nothingLoaded, pinned, playingAudio, downloading
    }

    public static func exemption(for tab: SleepCandidate, settings: SleepSettings) -> Exemption? {
        if tab.isSelected { return .selected }
        if tab.isAsleep { return .alreadyAsleep }
        if !tab.hasLoadedContent { return .nothingLoaded }
        if tab.isPlayingAudio { return .playingAudio }
        if tab.hasActiveDownload { return .downloading }
        if tab.isPinned, settings.neverUnloadPinned { return .pinned }
        return nil
    }

    /// Seconds of inactivity after which a tab is unloaded, or nil for never.
    public static func threshold(settings: SleepSettings, pressure: MemoryPressure) -> TimeInterval? {
        let configured = TimeInterval(settings.inactiveMinutes) * 60
        let normal: TimeInterval? = settings.unloadInactiveTabs ? configured : nil
        guard settings.respondToMemoryPressure else { return normal }
        switch pressure {
        case .normal: return normal
        case .warning: return max(60, configured / 4)
        case .critical: return 0
        }
    }

    /// The tabs to unload now, least recently used first.
    public static func tabsToUnload(
        _ tabs: [SleepCandidate], settings: SleepSettings, pressure: MemoryPressure, now: Date
    ) -> [UUID] {
        guard let threshold = threshold(settings: settings, pressure: pressure) else { return [] }
        return tabs
            .filter { exemption(for: $0, settings: settings) == nil && now.timeIntervalSince($0.lastActive) >= threshold }
            .sorted { $0.lastActive < $1.lastActive }
            .map(\.id)
    }
}
