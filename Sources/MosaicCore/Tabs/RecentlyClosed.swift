import Foundation

/// One tab as it was when it was closed: enough to bring it back where it was.
public struct ClosedTabRecord: Codable, Equatable, Sendable {
    public var engine: String
    public var url: String
    public var title: String
    public var workspaceID: UUID?
    public var groupID: UUID?
    public var isPinned: Bool
    public var isMuted: Bool
    /// Its position among its workspace's tabs.
    public var index: Int

    public init(
        engine: String, url: String, title: String, workspaceID: UUID?, groupID: UUID? = nil,
        isPinned: Bool = false, isMuted: Bool = false, index: Int = 0
    ) {
        self.engine = engine
        self.url = url
        self.title = title
        self.workspaceID = workspaceID
        self.groupID = groupID
        self.isPinned = isPinned
        self.isMuted = isMuted
        self.index = index
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        engine = (try? c.decodeIfPresent(String.self, forKey: .engine)) ?? "gecko"
        url = (try? c.decodeIfPresent(String.self, forKey: .url)) ?? ""
        title = (try? c.decodeIfPresent(String.self, forKey: .title)) ?? ""
        workspaceID = (try? c.decodeIfPresent(UUID.self, forKey: .workspaceID)) ?? nil
        groupID = (try? c.decodeIfPresent(UUID.self, forKey: .groupID)) ?? nil
        isPinned = (try? c.decodeIfPresent(Bool.self, forKey: .isPinned)) ?? false
        isMuted = (try? c.decodeIfPresent(Bool.self, forKey: .isMuted)) ?? false
        index = (try? c.decodeIfPresent(Int.self, forKey: .index)) ?? 0
    }

    public var displayTitle: String {
        if !title.isEmpty { return title }
        return URL(string: url)?.host ?? url
    }
}

/// One entry of the Recently Closed list: a single tab, or several tabs closed together
/// (a group or a workspace), which ⌘⇧T brings back as a whole.
public struct ClosedItem: Codable, Equatable, Identifiable, Sendable {
    public enum Kind: String, Codable, Sendable { case tab, group, workspace }

    public var id: UUID
    public var closedAt: Date
    public var kind: Kind
    public var tabs: [ClosedTabRecord]
    /// For `.group`: the group, so reopening recreates it.
    public var group: SessionGroup?
    /// For `.workspace`: the workspace, so reopening recreates it.
    public var workspace: SessionWorkspace?

    public init(
        id: UUID = UUID(), closedAt: Date = Date(), kind: Kind, tabs: [ClosedTabRecord],
        group: SessionGroup? = nil, workspace: SessionWorkspace? = nil
    ) {
        self.id = id
        self.closedAt = closedAt
        self.kind = kind
        self.tabs = tabs
        self.group = group
        self.workspace = workspace
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = (try? c.decodeIfPresent(UUID.self, forKey: .id)) ?? UUID()
        closedAt = (try? c.decodeIfPresent(Date.self, forKey: .closedAt)) ?? .distantPast
        kind = (try? c.decodeIfPresent(Kind.self, forKey: .kind)) ?? .tab
        tabs = (try? c.decodeIfPresent([ClosedTabRecord].self, forKey: .tabs)) ?? []
        group = (try? c.decodeIfPresent(SessionGroup.self, forKey: .group)) ?? nil
        workspace = (try? c.decodeIfPresent(SessionWorkspace.self, forKey: .workspace)) ?? nil
    }

    /// What a menu shows for the entry.
    public var title: String {
        switch kind {
        case .tab:
            return tabs.first?.displayTitle ?? "Tab"
        case .group:
            let name = group?.name.isEmpty == false ? group!.name : "Unnamed group"
            return "\(name) (\(tabs.count) \(tabs.count == 1 ? "tab" : "tabs"))"
        case .workspace:
            return "\(workspace?.name ?? "Workspace") (\(tabs.count) \(tabs.count == 1 ? "tab" : "tabs"))"
        }
    }
}

/// The Recently Closed list, newest first, capped at a number of entries.
public struct RecentlyClosedList: Equatable, Sendable {
    public private(set) var items: [ClosedItem]

    public init(_ items: [ClosedItem] = []) {
        self.items = items.filter { !$0.tabs.isEmpty }
    }

    /// Adds an entry at the front, dropping its start-page tabs (nothing to reopen) and the
    /// oldest entries beyond `limit`. An entry left with no tabs is not added.
    public mutating func push(_ item: ClosedItem, limit: Int) {
        var item = item
        item.tabs = item.tabs.filter { !$0.url.isEmpty }
        guard !item.tabs.isEmpty else { return }
        items.insert(item, at: 0)
        trim(to: limit)
    }

    /// Removes and returns the newest entry.
    public mutating func popLatest() -> ClosedItem? {
        items.isEmpty ? nil : items.removeFirst()
    }

    /// Removes and returns the entry with `id`.
    @discardableResult
    public mutating func remove(id: UUID) -> ClosedItem? {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return nil }
        return items.remove(at: index)
    }

    public mutating func trim(to limit: Int) {
        if items.count > max(limit, 0) { items.removeLast(items.count - max(limit, 0)) }
    }

    public mutating func removeAll() {
        items.removeAll()
    }
}
