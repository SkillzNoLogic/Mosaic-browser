namespace Mosaic.Core.Tabs;

/// <summary>What the ordering rules need to know about a tab.</summary>
public readonly record struct TabSlot(Guid Id, Guid WorkspaceId, bool IsPinned = false, Guid? GroupId = null);

/// <summary>
/// The ordering rules of the tab list. Port of <c>Sources/MosaicCore/Tabs/TabOrdering.swift</c>.
/// </summary>
/// <remarks>
/// Mosaic keeps every tab of every workspace in one list; each workspace's tabs are the
/// subsequence with its ID. Within a workspace:
/// <list type="number">
/// <item>pinned tabs come first;</item>
/// <item>pinned tabs are never in a group;</item>
/// <item>the members of a group are contiguous.</item>
/// </list>
/// Every function here returns a list that satisfies these rules. Only the order within a
/// workspace means anything; how workspaces interleave in the global list does not
/// (<see cref="Normalized"/> keeps it, the moves may gather a workspace's tabs together).
/// </remarks>
public static class TabOrdering
{
    /// <summary>
    /// Restores the rules with as little movement as possible: pinned tabs keep their relative
    /// order at the front, a group gathers at the position of its first member.
    /// </summary>
    public static List<TabSlot> Normalized(IReadOnlyList<TabSlot> slots)
    {
        var result = slots.ToList();
        foreach (var positions in PositionsByWorkspace(slots).Values)
        {
            var ordered = NormalizedWorkspace(positions.Select(i => slots[i]).ToList());
            for (var k = 0; k < positions.Count; k++) result[positions[k]] = ordered[k];
        }
        return result;
    }

    /// <summary>True if <paramref name="slots"/> already satisfies the rules.</summary>
    public static bool IsNormalized(IReadOnlyList<TabSlot> slots) => Normalized(slots).SequenceEqual(slots);

    /// <summary>
    /// Moves a tab so that it ends up at <paramref name="index"/> among its workspace's tabs
    /// (0 = first), for drag and drop. The index is clamped to the tab's section: a pinned tab
    /// stays among the pinned tabs and an unpinned one after them (pinning is a separate,
    /// explicit command). Dropped between two members of a group, the tab joins that group;
    /// dropped next to its own group it stays in it; anywhere else it leaves it.
    /// </summary>
    public static List<TabSlot> Move(IReadOnlyList<TabSlot> slots, Guid id, int index)
    {
        var at = slots.ToList().FindIndex(s => s.Id == id);
        if (at < 0) return slots.ToList();
        var moving = slots[at];
        var workspace = moving.WorkspaceId;
        var list = slots.Where(s => s.WorkspaceId == workspace && s.Id != id).ToList();
        var pinnedCount = list.TakeWhile(s => s.IsPinned).Count();
        var target = Math.Clamp(index, 0, list.Count);
        target = moving.IsPinned ? Math.Min(target, pinnedCount) : Math.Max(target, pinnedCount);

        var placed = moving;
        if (placed.IsPinned)
        {
            placed = placed with { GroupId = null };
        }
        else
        {
            TabSlot? previous = target > 0 ? list[target - 1] : null;
            TabSlot? next = target < list.Count ? list[target] : null;
            var previousGroup = previous is { IsPinned: false } p ? p.GroupId : null;
            if (previousGroup is { } group && group == next?.GroupId)
            {
                placed = placed with { GroupId = group };
            }
            else if (placed.GroupId is { } own && (previousGroup == own || next?.GroupId == own))
            {
                // At the edge of its own group: still a member.
            }
            else
            {
                placed = placed with { GroupId = null };
            }
        }
        list.Insert(target, placed);
        return Normalized(Replacing(workspace, list, slots));
    }

    /// <summary>
    /// Moves a whole group so that its first member ends up at <paramref name="index"/> among its
    /// workspace's tabs <i>not</i> counting the group's own members. Never lands among the pinned
    /// tabs or inside another group (it goes after that group instead).
    /// </summary>
    public static List<TabSlot> MoveGroup(IReadOnlyList<TabSlot> slots, Guid groupId, int index)
    {
        var members = slots.Where(s => s.GroupId == groupId && !s.IsPinned).ToList();
        if (members.Count == 0) return slots.ToList();
        var workspace = members[0].WorkspaceId;
        var list = slots.Where(s => s.WorkspaceId == workspace && !(s.GroupId == groupId && !s.IsPinned)).ToList();
        var pinnedCount = list.TakeWhile(s => s.IsPinned).Count();
        var target = Math.Max(Math.Clamp(index, 0, list.Count), pinnedCount);
        // Inside another group's span: move past its end.
        while (target > 0 && target < list.Count && list[target].GroupId is { } other && list[target - 1].GroupId == other)
        {
            target++;
        }
        list.InsertRange(target, members.Where(s => s.WorkspaceId == workspace));
        return Normalized(Replacing(workspace, list, slots));
    }

    /// <summary>
    /// Where a new tab goes, as an index into <paramref name="slots"/> to insert at, and the group
    /// it joins. <paramref name="anchor"/> is the tab it opens from (normally the selected tab). A
    /// tab opened from a link (<paramref name="joiningAnchorGroup"/>) goes right after its opener
    /// and joins its group; a plain new tab goes after the opener's group instead. A new tab never
    /// lands among the pinned tabs. Without a usable anchor (none, or in another workspace) it
    /// goes last.
    /// </summary>
    public static (int Index, Guid? GroupId) Insertion(
        IReadOnlyList<TabSlot> slots, Guid workspace, Guid? anchor, bool joiningAnchorGroup)
    {
        var positions = Enumerable.Range(0, slots.Count).Where(i => slots[i].WorkspaceId == workspace).ToList();
        if (positions.Count == 0) return (slots.Count, null);
        var last = positions[^1];
        var anchorIndex = anchor is { } a ? positions.FindIndex(i => slots[i].Id == a) : -1;
        if (anchorIndex < 0) return (last + 1, null);
        anchorIndex = positions[anchorIndex];
        var anchorSlot = slots[anchorIndex];
        if (anchorSlot.IsPinned)
        {
            var lastPinned = positions.LastOrDefault(i => slots[i].IsPinned, anchorIndex);
            return (lastPinned + 1, null);
        }
        if (anchorSlot.GroupId is { } group)
        {
            if (joiningAnchorGroup) return (anchorIndex + 1, group);
            var lastMember = positions.LastOrDefault(i => slots[i].GroupId == group, anchorIndex);
            return (lastMember + 1, null);
        }
        return (anchorIndex + 1, null);
    }

    /// <summary>
    /// Where a tab should go when it moves into <paramref name="workspace"/>: after that
    /// workspace's last tab (its pinned tabs, if it is pinned), as an index into
    /// <paramref name="slots"/> without the tab itself.
    /// </summary>
    public static int AppendIndex(IReadOnlyList<TabSlot> slots, Guid workspace, bool pinned)
    {
        var positions = Enumerable.Range(0, slots.Count).Where(i => slots[i].WorkspaceId == workspace).ToList();
        if (pinned)
        {
            var lastPinned = positions.LastOrDefault(i => slots[i].IsPinned, -1);
            if (lastPinned >= 0) return lastPinned + 1;
            if (positions.Count > 0) return positions[0];
        }
        return positions.Count > 0 ? positions[^1] + 1 : slots.Count;
    }

    // ---- Helpers ----

    private static Dictionary<Guid, List<int>> PositionsByWorkspace(IReadOnlyList<TabSlot> slots)
    {
        var positions = new Dictionary<Guid, List<int>>();
        for (var i = 0; i < slots.Count; i++)
        {
            if (!positions.TryGetValue(slots[i].WorkspaceId, out var list)) positions[slots[i].WorkspaceId] = list = [];
            list.Add(i);
        }
        return positions;
    }

    private static List<TabSlot> NormalizedWorkspace(List<TabSlot> members)
    {
        var cleaned = members.Select(s => s.IsPinned ? s with { GroupId = null } : s).ToList();
        var pinned = cleaned.Where(s => s.IsPinned);
        var unpinned = cleaned.Where(s => !s.IsPinned).ToList();
        var ordered = new List<TabSlot>();
        var gathered = new HashSet<Guid>();
        foreach (var slot in unpinned)
        {
            if (slot.GroupId is not { } group)
            {
                ordered.Add(slot);
                continue;
            }
            if (gathered.Add(group)) ordered.AddRange(unpinned.Where(s => s.GroupId == group));
        }
        return [.. pinned, .. ordered];
    }

    /// <summary>
    /// Writes <paramref name="list"/> (a workspace's tabs, possibly with a different count than
    /// before) back into the global list at the place that workspace occupies.
    /// </summary>
    private static List<TabSlot> Replacing(Guid workspace, List<TabSlot> list, IReadOnlyList<TabSlot> slots)
    {
        var result = new List<TabSlot>(slots.Count + 1);
        var inserted = false;
        foreach (var slot in slots)
        {
            if (slot.WorkspaceId == workspace)
            {
                if (inserted) continue;
                result.AddRange(list);
                inserted = true;
            }
            else
            {
                result.Add(slot);
            }
        }
        if (!inserted) result.AddRange(list);
        return result;
    }
}
