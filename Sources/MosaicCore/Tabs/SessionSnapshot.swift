import Foundation

// The saved shape of Mosaic's tabs, groups and workspaces (session restore, crash
// recovery, recently closed). Engine-independent: engines are stored by name so that a
// session written with an engine this build lacks still loads.
//
// Every type decodes tolerantly (missing keys take defaults) so that a file written by an
// older Mosaic, which lacked fields added since, still restores. `SessionCodec` refuses
// files from a newer format version instead of misreading them.

public struct SessionWorkspace: Codable, Equatable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var symbol: String

    public init(id: UUID = UUID(), name: String, symbol: String = "square.grid.2x2") {
        self.id = id
        self.name = name
        self.symbol = symbol
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        name = (try? c.decodeIfPresent(String.self, forKey: .name)) ?? "Workspace"
        symbol = (try? c.decodeIfPresent(String.self, forKey: .symbol)) ?? "square.grid.2x2"
    }
}

public struct SessionGroup: Codable, Equatable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    /// A `TabGroup.Color` raw value ("blue", "red", …).
    public var color: String
    public var isCollapsed: Bool

    public init(id: UUID = UUID(), name: String, color: String = "blue", isCollapsed: Bool = false) {
        self.id = id
        self.name = name
        self.color = color
        self.isCollapsed = isCollapsed
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        name = (try? c.decodeIfPresent(String.self, forKey: .name)) ?? ""
        color = (try? c.decodeIfPresent(String.self, forKey: .color)) ?? "blue"
        isCollapsed = (try? c.decodeIfPresent(Bool.self, forKey: .isCollapsed)) ?? false
    }
}

public struct SessionTab: Codable, Equatable, Sendable {
    public var id: UUID
    /// "gecko" or "chromium".
    public var engine: String
    /// Empty for a tab showing the start page.
    public var url: String
    public var title: String
    public var isPinned: Bool
    public var isMuted: Bool
    public var workspaceID: UUID?
    public var groupID: UUID?
    /// The selected tab of its workspace.
    public var isSelected: Bool

    public init(
        id: UUID = UUID(), engine: String, url: String, title: String = "", isPinned: Bool = false,
        isMuted: Bool = false, workspaceID: UUID?, groupID: UUID? = nil, isSelected: Bool = false
    ) {
        self.id = id
        self.engine = engine
        self.url = url
        self.title = title
        self.isPinned = isPinned
        self.isMuted = isMuted
        self.workspaceID = workspaceID
        self.groupID = groupID
        self.isSelected = isSelected
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = (try? c.decodeIfPresent(UUID.self, forKey: .id)) ?? UUID()
        engine = (try? c.decodeIfPresent(String.self, forKey: .engine)) ?? "gecko"
        url = (try? c.decodeIfPresent(String.self, forKey: .url)) ?? ""
        title = (try? c.decodeIfPresent(String.self, forKey: .title)) ?? ""
        isPinned = (try? c.decodeIfPresent(Bool.self, forKey: .isPinned)) ?? false
        isMuted = (try? c.decodeIfPresent(Bool.self, forKey: .isMuted)) ?? false
        workspaceID = (try? c.decodeIfPresent(UUID.self, forKey: .workspaceID)) ?? nil
        groupID = (try? c.decodeIfPresent(UUID.self, forKey: .groupID)) ?? nil
        isSelected = (try? c.decodeIfPresent(Bool.self, forKey: .isSelected)) ?? false
    }
}

/// Everything session restore brings back.
public struct SessionSnapshot: Codable, Equatable, Sendable {
    /// Format version written by this build. Bump it only for changes older builds would
    /// misread; adding an optional field does not need a bump.
    public static let currentVersion = 1

    public var version: Int
    public var savedAt: Date
    public var workspaces: [SessionWorkspace]
    public var selectedWorkspaceID: UUID?
    public var groups: [SessionGroup]
    /// All tabs of all workspaces, in tab-strip order.
    public var tabs: [SessionTab]
    public var recentlyClosed: [ClosedItem]

    public init(
        savedAt: Date = Date(), workspaces: [SessionWorkspace], selectedWorkspaceID: UUID?,
        groups: [SessionGroup] = [], tabs: [SessionTab] = [], recentlyClosed: [ClosedItem] = []
    ) {
        version = Self.currentVersion
        self.savedAt = savedAt
        self.workspaces = workspaces
        self.selectedWorkspaceID = selectedWorkspaceID
        self.groups = groups
        self.tabs = tabs
        self.recentlyClosed = recentlyClosed
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = (try? c.decodeIfPresent(Int.self, forKey: .version)) ?? 1
        savedAt = (try? c.decodeIfPresent(Date.self, forKey: .savedAt)) ?? .distantPast
        workspaces = (try? c.decodeIfPresent([SessionWorkspace].self, forKey: .workspaces)) ?? []
        selectedWorkspaceID = (try? c.decodeIfPresent(UUID.self, forKey: .selectedWorkspaceID)) ?? nil
        groups = (try? c.decodeIfPresent([SessionGroup].self, forKey: .groups)) ?? []
        tabs = (try? c.decodeIfPresent([SessionTab].self, forKey: .tabs)) ?? []
        recentlyClosed = (try? c.decodeIfPresent([ClosedItem].self, forKey: .recentlyClosed)) ?? []
    }

    /// Tabs that would show something when restored (not the start page).
    public var pageCount: Int { tabs.filter { !$0.url.isEmpty }.count }

    /// Makes the snapshot internally consistent, whatever wrote it:
    /// at least one workspace; unique IDs; every tab in a known workspace; group references
    /// that exist, belong to one workspace and are not on pinned tabs; groups contiguous and
    /// pinned tabs first; no empty groups; at most one selected tab per workspace; a valid
    /// selected workspace; no empty recently-closed entries.
    public func repaired() -> SessionSnapshot {
        var s = self
        var seenWorkspaces = Set<UUID>()
        s.workspaces = s.workspaces.filter { seenWorkspaces.insert($0.id).inserted }
        if s.workspaces.isEmpty {
            s.workspaces = [SessionWorkspace(name: "Personal", symbol: "person")]
        }
        let workspaceIDs = Set(s.workspaces.map(\.id))
        let fallbackWorkspace = s.workspaces[0].id

        var seenGroups = Set<UUID>()
        s.groups = s.groups.filter { seenGroups.insert($0.id).inserted }
        let groupIDs = Set(s.groups.map(\.id))

        var seenTabs = Set<UUID>()
        var groupWorkspace: [UUID: UUID] = [:]
        var selectedIn = Set<UUID>()
        s.tabs = s.tabs.compactMap { tab in
            guard seenTabs.insert(tab.id).inserted else { return nil }
            var tab = tab
            if tab.workspaceID.map({ !workspaceIDs.contains($0) }) ?? true { tab.workspaceID = fallbackWorkspace }
            let workspace = tab.workspaceID ?? fallbackWorkspace
            if let group = tab.groupID {
                if tab.isPinned || !groupIDs.contains(group) {
                    tab.groupID = nil
                } else if let owner = groupWorkspace[group], owner != workspace {
                    tab.groupID = nil
                } else {
                    groupWorkspace[group] = workspace
                }
            }
            if tab.isSelected, !selectedIn.insert(workspace).inserted { tab.isSelected = false }
            return tab
        }
        let usedGroups = Set(s.tabs.compactMap(\.groupID))
        s.groups = s.groups.filter { usedGroups.contains($0.id) }

        if s.selectedWorkspaceID.map({ !workspaceIDs.contains($0) }) ?? true {
            s.selectedWorkspaceID = fallbackWorkspace
        }

        let slots = s.tabs.map {
            TabSlot(id: $0.id, workspaceID: $0.workspaceID ?? fallbackWorkspace, isPinned: $0.isPinned, groupID: $0.groupID)
        }
        let byID = Dictionary(uniqueKeysWithValues: s.tabs.map { ($0.id, $0) })
        s.tabs = TabOrdering.normalized(slots).compactMap { slot in
            byID[slot.id].map { tab in
                var tab = tab
                tab.groupID = slot.groupID
                return tab
            }
        }
        s.recentlyClosed = s.recentlyClosed.filter { !$0.tabs.isEmpty }
        return s
    }
}

/// Reads and writes session files.
public enum SessionCodec {
    public enum DecodeError: Error, Equatable {
        /// Written by a newer Mosaic in a format this build does not understand.
        case newerVersion(Int)
        /// Not a session file at all (truncated, not JSON, wrong shape).
        case malformed
    }

    public static func encode(_ snapshot: SessionSnapshot) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(snapshot)
    }

    /// Decodes and repairs a session file. A file without a `version` is read as version 1.
    public static func decode(_ data: Data) throws -> SessionSnapshot {
        struct Header: Decodable { let version: Int? }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let header = try? decoder.decode(Header.self, from: data) else { throw DecodeError.malformed }
        let version = header.version ?? 1
        guard version <= SessionSnapshot.currentVersion else { throw DecodeError.newerVersion(version) }
        guard var snapshot = try? decoder.decode(SessionSnapshot.self, from: data) else { throw DecodeError.malformed }
        snapshot.version = SessionSnapshot.currentVersion
        return snapshot.repaired()
    }
}
