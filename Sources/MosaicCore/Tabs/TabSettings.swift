import Foundation

/// The user's choices on the Tabs & Workspaces and Performance settings pages.
///
/// Stored as one JSON value in Mosaic's preferences. Decoding is tolerant: a key that is
/// missing (written by an older Mosaic) or out of range falls back to its default, so a new
/// setting never resets the others.
public struct TabSettings: Codable, Equatable, Sendable {
    /// What a launch shows.
    public enum Startup: String, Codable, CaseIterable, Sendable {
        /// Every tab, group and workspace from the last run (tabs stay unloaded until selected).
        case previousSession
        /// One new tab. Workspaces are always kept; pinned tabs too if `keepPinnedTabs`.
        case startPage
    }

    public var startup: Startup
    /// With `startPage`, still bring back pinned tabs (Safari and Arc behave this way).
    public var keepPinnedTabs: Bool
    /// ⌘W on a pinned tab only closes it when pressed twice in a row.
    public var protectPinnedTabs: Bool
    /// When an address is already open in another tab, offer to switch to it.
    public var offerSwitchToOpenTab: Bool
    /// How many closed tabs (or closed groups/workspaces) ⌘⇧T can bring back.
    public var recentlyClosedLimit: Int
    public var sleep: SleepSettings

    public static let recentlyClosedRange = 5...100

    public init(
        startup: Startup = .previousSession, keepPinnedTabs: Bool = true, protectPinnedTabs: Bool = true,
        offerSwitchToOpenTab: Bool = true, recentlyClosedLimit: Int = 25, sleep: SleepSettings = SleepSettings()
    ) {
        self.startup = startup
        self.keepPinnedTabs = keepPinnedTabs
        self.protectPinnedTabs = protectPinnedTabs
        self.offerSwitchToOpenTab = offerSwitchToOpenTab
        self.recentlyClosedLimit = recentlyClosedLimit.clamped(to: Self.recentlyClosedRange)
        self.sleep = sleep
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = TabSettings()
        startup = (try? c.decodeIfPresent(Startup.self, forKey: .startup)) ?? defaults.startup
        keepPinnedTabs = (try? c.decodeIfPresent(Bool.self, forKey: .keepPinnedTabs)) ?? defaults.keepPinnedTabs
        protectPinnedTabs = (try? c.decodeIfPresent(Bool.self, forKey: .protectPinnedTabs)) ?? defaults.protectPinnedTabs
        offerSwitchToOpenTab = (try? c.decodeIfPresent(Bool.self, forKey: .offerSwitchToOpenTab))
            ?? defaults.offerSwitchToOpenTab
        recentlyClosedLimit = ((try? c.decodeIfPresent(Int.self, forKey: .recentlyClosedLimit)) ?? nil)
            .map { $0.clamped(to: Self.recentlyClosedRange) } ?? defaults.recentlyClosedLimit
        sleep = (try? c.decodeIfPresent(SleepSettings.self, forKey: .sleep)) ?? defaults.sleep
    }

    /// Settings from stored JSON; defaults for anything missing or unreadable.
    public static func decode(_ data: Data?) -> TabSettings {
        guard let data, let settings = try? JSONDecoder().decode(TabSettings.self, from: data) else {
            return TabSettings()
        }
        return settings
    }

    public func encoded() -> Data {
        (try? JSONEncoder().encode(self)) ?? Data()
    }
}

/// Sleeping tabs: unloading background tabs to free memory.
public struct SleepSettings: Codable, Equatable, Sendable {
    public var unloadInactiveTabs: Bool
    /// A background tab is unloaded after this many minutes without being selected.
    public var inactiveMinutes: Int
    public var neverUnloadPinned: Bool
    /// When macOS reports memory pressure, unload sooner (warning) or every eligible
    /// background tab (critical).
    public var respondToMemoryPressure: Bool

    public static let minuteChoices = [5, 15, 30, 60, 120, 240, 480]

    public init(
        unloadInactiveTabs: Bool = true, inactiveMinutes: Int = 60, neverUnloadPinned: Bool = true,
        respondToMemoryPressure: Bool = true
    ) {
        self.unloadInactiveTabs = unloadInactiveTabs
        self.inactiveMinutes = inactiveMinutes.clamped(to: 1...1440)
        self.neverUnloadPinned = neverUnloadPinned
        self.respondToMemoryPressure = respondToMemoryPressure
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = SleepSettings()
        self.init(
            unloadInactiveTabs: (try? c.decodeIfPresent(Bool.self, forKey: .unloadInactiveTabs))
                ?? defaults.unloadInactiveTabs,
            inactiveMinutes: (try? c.decodeIfPresent(Int.self, forKey: .inactiveMinutes)) ?? defaults.inactiveMinutes,
            neverUnloadPinned: (try? c.decodeIfPresent(Bool.self, forKey: .neverUnloadPinned))
                ?? defaults.neverUnloadPinned,
            respondToMemoryPressure: (try? c.decodeIfPresent(Bool.self, forKey: .respondToMemoryPressure))
                ?? defaults.respondToMemoryPressure)
    }
}

fileprivate extension Comparable {
    func clamped(to range: ClosedRange<Self>) -> Self {
        min(max(self, range.lowerBound), range.upperBound)
    }
}
