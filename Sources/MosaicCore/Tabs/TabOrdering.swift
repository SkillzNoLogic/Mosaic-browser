import Foundation

/// What the ordering rules need to know about a tab.
public struct TabSlot: Equatable, Sendable {
    public var id: UUID
    public var workspaceID: UUID
    public var isPinned: Bool
    public var groupID: UUID?

    public init(id: UUID, workspaceID: UUID, isPinned: Bool = false, groupID: UUID? = nil) {
        self.id = id
        self.workspaceID = workspaceID
        self.isPinned = isPinned
        self.groupID = groupID
    }
}

/// The ordering rules of the tab list.
///
/// Mosaic keeps every tab of every workspace in one array; each workspace's tabs are the
/// subsequence with its ID. Within a workspace:
/// 1. pinned tabs come first;
/// 2. pinned tabs are never in a group;
/// 3. the members of a group are contiguous.
/// Every function here returns a list that satisfies these rules. Only the order within a
/// workspace means anything; how workspaces interleave in the global array does not
/// (`normalized` keeps it, the moves may gather a workspace's tabs together).
public enum TabOrdering {
    /// Restores the rules with as little movement as possible: pinned tabs keep their
    /// relative order at the front, a group gathers at the position of its first member.
    public static func normalized(_ slots: [TabSlot]) -> [TabSlot] {
        var result = slots
        for (_, positions) in positionsByWorkspace(slots) {
            let ordered = normalizedWorkspace(positions.map { slots[$0] })
            for (k, i) in positions.enumerated() { result[i] = ordered[k] }
        }
        return result
    }

    /// True if `slots` already satisfies the rules.
    public static func isNormalized(_ slots: [TabSlot]) -> Bool {
        normalized(slots) == slots
    }

    /// Moves a tab so that it ends up at `index` among its workspace's tabs (0 = first),
    /// for drag and drop. The index is clamped to the tab's section: a pinned tab stays
    /// among the pinned tabs and an unpinned one after them (pinning is a separate,
    /// explicit command). Dropped between two members of a group, the tab joins that
    /// group; dropped next to its own group it stays in it; anywhere else it leaves it.
    public static func move(_ slots: [TabSlot], id: UUID, to index: Int) -> [TabSlot] {
        guard let moving = slots.first(where: { $0.id == id }) else { return slots }
        let workspace = moving.workspaceID
        var list = slots.filter { $0.workspaceID == workspace && $0.id != id }
        let pinnedCount = list.prefix { $0.isPinned }.count
        var target = min(max(index, 0), list.count)
        target = moving.isPinned ? min(target, pinnedCount) : max(target, pinnedCount)

        var placed = moving
        if placed.isPinned {
            placed.groupID = nil
        } else {
            let previous = target > 0 ? list[target - 1] : nil
            let next = target < list.count ? list[target] : nil
            let previousGroup = previous?.isPinned == false ? previous?.groupID : nil
            if let group = previousGroup, group == next?.groupID {
                placed.groupID = group
            } else if let own = placed.groupID, previousGroup == own || next?.groupID == own {
                // At the edge of its own group: still a member.
            } else {
                placed.groupID = nil
            }
        }
        list.insert(placed, at: target)
        return normalized(replacing(workspace, with: list, in: slots))
    }

    /// Moves a whole group so that its first member ends up at `index` among its
    /// workspace's tabs *not* counting the group's own members. Never lands among the
    /// pinned tabs or inside another group (it goes after that group instead).
    public static func moveGroup(_ slots: [TabSlot], groupID: UUID, to index: Int) -> [TabSlot] {
        let members = slots.filter { $0.groupID == groupID && !$0.isPinned }
        guard let workspace = members.first?.workspaceID else { return slots }
        var list = slots.filter { $0.workspaceID == workspace && !($0.groupID == groupID && !$0.isPinned) }
        let pinnedCount = list.prefix { $0.isPinned }.count
        var target = max(min(max(index, 0), list.count), pinnedCount)
        // Inside another group's span: move past its end.
        while target > 0, target < list.count, let other = list[target].groupID, list[target - 1].groupID == other {
            target += 1
        }
        list.insert(contentsOf: members.filter { $0.workspaceID == workspace }, at: target)
        return normalized(replacing(workspace, with: list, in: slots))
    }

    /// Where a new tab goes, as an index into `slots` to insert at, and the group it joins.
    ///
    /// `anchor` is the tab it opens from (normally the selected tab). A tab opened from a
    /// link (`joiningAnchorGroup`) goes right after its opener and joins its group; a plain
    /// new tab goes after the opener's group instead. A new tab never lands among the
    /// pinned tabs. Without a usable anchor (none, or in another workspace) it goes last.
    public static func insertion(
        in slots: [TabSlot], workspace: UUID, after anchor: UUID?, joiningAnchorGroup: Bool
    ) -> (index: Int, groupID: UUID?) {
        let positions = slots.indices.filter { slots[$0].workspaceID == workspace }
        guard let last = positions.last else { return (slots.count, nil) }
        guard let anchor, let anchorIndex = positions.first(where: { slots[$0].id == anchor }) else {
            return (last + 1, nil)
        }
        let anchorSlot = slots[anchorIndex]
        if anchorSlot.isPinned {
            let lastPinned = positions.last { slots[$0].isPinned } ?? anchorIndex
            return (lastPinned + 1, nil)
        }
        if let group = anchorSlot.groupID {
            if joiningAnchorGroup { return (anchorIndex + 1, group) }
            let lastMember = positions.last { slots[$0].groupID == group } ?? anchorIndex
            return (lastMember + 1, nil)
        }
        return (anchorIndex + 1, nil)
    }

    /// Where a tab should go when it moves into `workspace`: after that workspace's last tab
    /// (its pinned tabs, if it is pinned), as an index into `slots` without the tab itself.
    public static func appendIndex(in slots: [TabSlot], workspace: UUID, pinned: Bool) -> Int {
        let positions = slots.indices.filter { slots[$0].workspaceID == workspace }
        if pinned, let lastPinned = positions.last(where: { slots[$0].isPinned }) { return lastPinned + 1 }
        if pinned, let first = positions.first { return first }
        return (positions.last.map { $0 + 1 }) ?? slots.count
    }

    // MARK: - Helpers

    private static func positionsByWorkspace(_ slots: [TabSlot]) -> [UUID: [Int]] {
        var positions: [UUID: [Int]] = [:]
        for (i, slot) in slots.enumerated() { positions[slot.workspaceID, default: []].append(i) }
        return positions
    }

    private static func normalizedWorkspace(_ members: [TabSlot]) -> [TabSlot] {
        let cleaned = members.map { slot -> TabSlot in
            var slot = slot
            if slot.isPinned { slot.groupID = nil }
            return slot
        }
        let pinned = cleaned.filter(\.isPinned)
        let unpinned = cleaned.filter { !$0.isPinned }
        var ordered: [TabSlot] = []
        var gathered = Set<UUID>()
        for slot in unpinned {
            guard let group = slot.groupID else {
                ordered.append(slot)
                continue
            }
            if gathered.insert(group).inserted {
                ordered.append(contentsOf: unpinned.filter { $0.groupID == group })
            }
        }
        return pinned + ordered
    }

    /// Writes `list` (a workspace's tabs, possibly with a different count than before)
    /// back into the global array at the place that workspace occupies.
    private static func replacing(_ workspace: UUID, with list: [TabSlot], in slots: [TabSlot]) -> [TabSlot] {
        var result: [TabSlot] = []
        var inserted = false
        for slot in slots {
            if slot.workspaceID == workspace {
                if !inserted {
                    result.append(contentsOf: list)
                    inserted = true
                }
            } else {
                result.append(slot)
            }
        }
        if !inserted { result.append(contentsOf: list) }
        return result
    }
}
