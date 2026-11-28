namespace Mosaic.Core.Tabs;

/// <summary>
/// One tab as it was when it was closed: enough to bring it back where it was.
/// Port of <c>ClosedTabRecord</c> (<c>Sources/MosaicCore/Tabs/RecentlyClosed.swift</c>).
/// </summary>
/// <remarks><c>Engine</c>: "gecko" or "chromium".</remarks>
/// <remarks><c>Index</c>: Its position among its workspace's tabs.</remarks>
public sealed record ClosedTabRecord(
    string Engine, string Url, string Title, Guid? WorkspaceId, Guid? GroupId = null,
    bool IsPinned = false, bool IsMuted = false, int Index = 0)
{
    public string DisplayTitle =>
        Title.Length > 0 ? Title
        : Uri.TryCreate(Url, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 ? parsed.Host
        : Url;
}

public enum ClosedItemKind { Tab, Group, Workspace }

/// <summary>
/// One entry of the Recently Closed list: a single tab, or several tabs closed together (a group
/// or a workspace), which Ctrl+Shift+T brings back as a whole.
/// </summary>
public sealed record ClosedItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset ClosedAt { get; init; } = DateTimeOffset.UtcNow;
    public ClosedItemKind Kind { get; init; }
    public IReadOnlyList<ClosedTabRecord> Tabs { get; init; } = [];
    /// <summary>For <see cref="ClosedItemKind.Group"/>: the group, so reopening recreates it.</summary>
    public SessionGroup? Group { get; init; }
    /// <summary>For <see cref="ClosedItemKind.Workspace"/>: the workspace, so reopening recreates it.</summary>
    public SessionWorkspace? Workspace { get; init; }

    /// <summary>What a menu shows for the entry.</summary>
    public string Title => Kind switch
    {
        ClosedItemKind.Group => $"{(Group is { Name.Length: > 0 } g ? g.Name : "Unnamed group")} ({Count(Tabs.Count)})",
        ClosedItemKind.Workspace => $"{Workspace?.Name ?? "Workspace"} ({Count(Tabs.Count)})",
        _ => Tabs.Count > 0 ? Tabs[0].DisplayTitle : "Tab",
    };

    private static string Count(int n) => n == 1 ? "1 tab" : $"{n} tabs";
}

/// <summary>The Recently Closed list, newest first, capped at a number of entries.</summary>
public sealed class RecentlyClosedList
{
    private readonly List<ClosedItem> items;

    public RecentlyClosedList(IEnumerable<ClosedItem>? items = null) =>
        this.items = items?.Where(item => item.Tabs.Count > 0).ToList() ?? [];

    public IReadOnlyList<ClosedItem> Items => items;

    /// <summary>
    /// Adds an entry at the front, dropping its start-page tabs (nothing to reopen) and the oldest
    /// entries beyond <paramref name="limit"/>. An entry left with no tabs is not added.
    /// </summary>
    public void Push(ClosedItem item, int limit)
    {
        var tabs = item.Tabs.Where(tab => tab.Url.Length > 0).ToList();
        if (tabs.Count == 0) return;
        items.Insert(0, item with { Tabs = tabs });
        Trim(limit);
    }

    /// <summary>Removes and returns the newest entry.</summary>
    public ClosedItem? PopLatest()
    {
        if (items.Count == 0) return null;
        var first = items[0];
        items.RemoveAt(0);
        return first;
    }

    /// <summary>Removes and returns the entry with <paramref name="id"/>.</summary>
    public ClosedItem? Remove(Guid id)
    {
        var index = items.FindIndex(item => item.Id == id);
        if (index < 0) return null;
        var item = items[index];
        items.RemoveAt(index);
        return item;
    }

    public void Trim(int limit)
    {
        var keep = Math.Max(limit, 0);
        if (items.Count > keep) items.RemoveRange(keep, items.Count - keep);
    }

    public void Clear() => items.Clear();
}
