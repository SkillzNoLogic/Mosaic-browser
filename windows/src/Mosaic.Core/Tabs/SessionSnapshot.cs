using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Tabs;

// The saved shape of Mosaic's tabs, groups and workspaces (session restore, crash recovery,
// recently closed). Port of Sources/MosaicCore/Tabs/SessionSnapshot.swift, with the same JSON
// (key names, UUID strings, ISO 8601 dates without fractions) so a session file moves between
// macOS and Windows. Engines are stored by name so that a session written with an engine this
// build lacks still loads. Every type decodes tolerantly (missing or mistyped keys take
// defaults); SessionCodec refuses files from a newer format version instead of misreading them.

public sealed record SessionWorkspace(Guid Id, string Name, string Symbol = SessionWorkspace.DefaultSymbol)
{
    public const string DefaultSymbol = "square.grid.2x2";
}

/// <remarks><c>Color</c>: A <see cref="TabGroupColor"/> name in lower case ("blue", "red", …).</remarks>
public sealed record SessionGroup(Guid Id, string Name, string Color = "blue", bool IsCollapsed = false);

public sealed record SessionTab
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>"gecko" or "chromium".</summary>
    public string Engine { get; init; } = "gecko";
    /// <summary>Empty for a tab showing the start page.</summary>
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsPinned { get; init; }
    public bool IsMuted { get; init; }
    public Guid? WorkspaceId { get; init; }
    public Guid? GroupId { get; init; }
    /// <summary>The selected tab of its workspace.</summary>
    public bool IsSelected { get; init; }
}

/// <summary>Everything session restore brings back.</summary>
public sealed record SessionSnapshot
{
    /// <summary>
    /// Format version written by this build. Bump it only for changes older builds would misread;
    /// adding an optional field does not need a bump.
    /// </summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<SessionWorkspace> Workspaces { get; init; } = [];
    public Guid? SelectedWorkspaceId { get; init; }
    public IReadOnlyList<SessionGroup> Groups { get; init; } = [];
    /// <summary>All tabs of all workspaces, in tab-strip order.</summary>
    public IReadOnlyList<SessionTab> Tabs { get; init; } = [];
    public IReadOnlyList<ClosedItem> RecentlyClosed { get; init; } = [];

    /// <summary>Tabs that would show something when restored (not the start page).</summary>
    public int PageCount => Tabs.Count(tab => tab.Url.Length > 0);

    /// <summary>
    /// Makes the snapshot internally consistent, whatever wrote it: at least one workspace; unique
    /// IDs; every tab in a known workspace; group references that exist, belong to one workspace
    /// and are not on pinned tabs; groups contiguous and pinned tabs first; no empty groups; at
    /// most one selected tab per workspace; a valid selected workspace; no empty recently-closed
    /// entries.
    /// </summary>
    public SessionSnapshot Repaired()
    {
        var seenWorkspaces = new HashSet<Guid>();
        var workspaces = Workspaces.Where(w => seenWorkspaces.Add(w.Id)).ToList();
        if (workspaces.Count == 0) workspaces.Add(new SessionWorkspace(Guid.NewGuid(), "Personal", "person"));
        var workspaceIds = workspaces.Select(w => w.Id).ToHashSet();
        var fallback = workspaces[0].Id;

        var seenGroups = new HashSet<Guid>();
        var groups = Groups.Where(g => seenGroups.Add(g.Id)).ToList();
        var groupIds = groups.Select(g => g.Id).ToHashSet();

        var seenTabs = new HashSet<Guid>();
        var groupWorkspace = new Dictionary<Guid, Guid>();
        var selectedIn = new HashSet<Guid>();
        var tabs = new List<SessionTab>();
        foreach (var original in Tabs)
        {
            if (!seenTabs.Add(original.Id)) continue;
            var tab = original;
            if (tab.WorkspaceId is not { } ws || !workspaceIds.Contains(ws)) tab = tab with { WorkspaceId = fallback };
            var workspace = tab.WorkspaceId!.Value;
            if (tab.GroupId is { } group)
            {
                if (tab.IsPinned || !groupIds.Contains(group)) tab = tab with { GroupId = null };
                else if (groupWorkspace.TryGetValue(group, out var owner) && owner != workspace) tab = tab with { GroupId = null };
                else groupWorkspace[group] = workspace;
            }
            if (tab.IsSelected && !selectedIn.Add(workspace)) tab = tab with { IsSelected = false };
            tabs.Add(tab);
        }
        var usedGroups = tabs.Where(t => t.GroupId is not null).Select(t => t.GroupId!.Value).ToHashSet();
        groups = groups.Where(g => usedGroups.Contains(g.Id)).ToList();

        var selectedWorkspace = SelectedWorkspaceId is { } sel && workspaceIds.Contains(sel) ? sel : fallback;

        var slots = tabs.Select(t => new TabSlot(t.Id, t.WorkspaceId ?? fallback, t.IsPinned, t.GroupId)).ToList();
        var byId = tabs.ToDictionary(t => t.Id);
        var ordered = TabOrdering.Normalized(slots).Select(slot => byId[slot.Id] with { GroupId = slot.GroupId }).ToList();

        return this with
        {
            Workspaces = workspaces,
            Groups = groups,
            Tabs = ordered,
            SelectedWorkspaceId = selectedWorkspace,
            RecentlyClosed = RecentlyClosed.Where(item => item.Tabs.Count > 0).ToList(),
        };
    }
}

/// <summary>A session file that cannot be used.</summary>
public sealed class SessionDecodeException : Exception
{
    private SessionDecodeException(string message, int? newerVersion) : base(message) => NewerVersion = newerVersion;

    /// <summary>Set when the file was written by a newer Mosaic in a format this build does not understand.</summary>
    public int? NewerVersion { get; }

    public bool IsMalformed => NewerVersion is null;

    internal static SessionDecodeException Newer(int version) =>
        new($"The session was saved by a newer version of Mosaic (format {version}).", version);

    internal static SessionDecodeException Malformed() => new("The session file is damaged.", null);
}

/// <summary>Reads and writes session files (the JSON of <c>SessionCodec</c> on macOS).</summary>
public static class SessionCodec
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static string Encode(SessionSnapshot snapshot)
    {
        var root = new JsonObject
        {
            ["version"] = snapshot.Version,
            ["savedAt"] = SessionJson.Date(snapshot.SavedAt),
            ["workspaces"] = new JsonArray([.. snapshot.Workspaces.Select(SessionJson.Encode)]),
            ["groups"] = new JsonArray([.. snapshot.Groups.Select(SessionJson.Encode)]),
            ["tabs"] = new JsonArray([.. snapshot.Tabs.Select(SessionJson.Encode)]),
            ["recentlyClosed"] = new JsonArray([.. snapshot.RecentlyClosed.Select(SessionJson.Encode)]),
        };
        if (snapshot.SelectedWorkspaceId is { } selected) root["selectedWorkspaceID"] = SessionJson.Id(selected);
        return SessionJson.Sorted(root)!.ToJsonString(Pretty);
    }

    /// <summary>Decodes and repairs a session file. A file without a <c>version</c> is read as version 1.</summary>
    /// <exception cref="SessionDecodeException">Not a session file, or one from a newer Mosaic.</exception>
    public static SessionSnapshot Decode(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw SessionDecodeException.Malformed();
        }
        catch (JsonException)
        {
            throw SessionDecodeException.Malformed();
        }
        var version = SessionJson.Int(root["version"]) ?? 1;
        if (version > SessionSnapshot.CurrentVersion) throw SessionDecodeException.Newer(version);
        var snapshot = new SessionSnapshot
        {
            Version = SessionSnapshot.CurrentVersion,
            SavedAt = SessionJson.ReadDate(root["savedAt"]) ?? DateTimeOffset.MinValue,
            Workspaces = SessionJson.List(root["workspaces"], SessionJson.DecodeWorkspace),
            SelectedWorkspaceId = SessionJson.ReadId(root["selectedWorkspaceID"]),
            Groups = SessionJson.List(root["groups"], SessionJson.DecodeGroup),
            Tabs = SessionJson.List(root["tabs"], SessionJson.DecodeTab),
            RecentlyClosed = SessionJson.List(root["recentlyClosed"], SessionJson.DecodeClosedItem),
        };
        return snapshot.Repaired();
    }
}

/// <summary>JSON mapping shared by sessions and Recently Closed, matching Swift's Codable output.</summary>
internal static class SessionJson
{
    // ---- Scalars ----

    /// <summary>Swift's <c>UUID.uuidString</c>: upper case with hyphens.</summary>
    public static string Id(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>Swift's <c>.iso8601</c> strategy: UTC, whole seconds (it rejects fractions).</summary>
    public static string Date(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

    public static Guid? ReadId(JsonNode? node) => Text(node) is { } text && Guid.TryParse(text, out var id) ? id : null;

    public static DateTimeOffset? ReadDate(JsonNode? node) =>
        Text(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;

    public static int? Int(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out int number) ? number
        : node is JsonValue other && other.GetValueKind() == JsonValueKind.Number && other.TryGetValue(out double real)
            && real is >= int.MinValue and <= int.MaxValue ? (int)real
        : null;

    /// <summary>Decodes each object of an array, skipping entries that are not objects or fail.</summary>
    public static List<T> List<T>(JsonNode? node, Func<JsonObject, T?> decode) where T : class
    {
        var result = new List<T>();
        if (node is not JsonArray array) return result;
        foreach (var entry in array)
        {
            if (entry is JsonObject item && decode(item) is { } decoded) result.Add(decoded);
        }
        return result;
    }

    /// <summary>A copy with every object's keys in ordinal order (Swift's <c>.sortedKeys</c>).</summary>
    public static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sorted)]),
        null => null,
        _ => node.DeepClone(),
    };

    // ---- Workspaces and groups ----

    public static JsonNode Encode(SessionWorkspace workspace) => new JsonObject
    {
        ["id"] = Id(workspace.Id),
        ["name"] = workspace.Name,
        ["symbol"] = workspace.Symbol,
    };

    public static SessionWorkspace? DecodeWorkspace(JsonObject o) =>
        ReadId(o["id"]) is { } id
            ? new SessionWorkspace(id, Text(o["name"]) ?? "Workspace", Text(o["symbol"]) ?? SessionWorkspace.DefaultSymbol)
            : null;

    public static JsonNode Encode(SessionGroup group) => new JsonObject
    {
        ["id"] = Id(group.Id),
        ["name"] = group.Name,
        ["color"] = group.Color,
        ["isCollapsed"] = group.IsCollapsed,
    };

    public static SessionGroup? DecodeGroup(JsonObject o) =>
        ReadId(o["id"]) is { } id
            ? new SessionGroup(id, Text(o["name"]) ?? "", Text(o["color"]) ?? "blue", Bool(o["isCollapsed"]) ?? false)
            : null;

    // ---- Tabs ----

    public static JsonNode Encode(SessionTab tab)
    {
        var o = new JsonObject
        {
            ["id"] = Id(tab.Id),
            ["engine"] = tab.Engine,
            ["url"] = tab.Url,
            ["title"] = tab.Title,
            ["isPinned"] = tab.IsPinned,
            ["isMuted"] = tab.IsMuted,
            ["isSelected"] = tab.IsSelected,
        };
        if (tab.WorkspaceId is { } ws) o["workspaceID"] = Id(ws);
        if (tab.GroupId is { } g) o["groupID"] = Id(g);
        return o;
    }

    public static SessionTab DecodeTab(JsonObject o) => new()
    {
        Id = ReadId(o["id"]) ?? Guid.NewGuid(),
        Engine = Text(o["engine"]) ?? "gecko",
        Url = Text(o["url"]) ?? "",
        Title = Text(o["title"]) ?? "",
        IsPinned = Bool(o["isPinned"]) ?? false,
        IsMuted = Bool(o["isMuted"]) ?? false,
        WorkspaceId = ReadId(o["workspaceID"]),
        GroupId = ReadId(o["groupID"]),
        IsSelected = Bool(o["isSelected"]) ?? false,
    };

    // ---- Recently closed ----

    public static JsonNode Encode(ClosedTabRecord record)
    {
        var o = new JsonObject
        {
            ["engine"] = record.Engine,
            ["url"] = record.Url,
            ["title"] = record.Title,
            ["isPinned"] = record.IsPinned,
            ["isMuted"] = record.IsMuted,
            ["index"] = record.Index,
        };
        if (record.WorkspaceId is { } ws) o["workspaceID"] = Id(ws);
        if (record.GroupId is { } g) o["groupID"] = Id(g);
        return o;
    }

    public static ClosedTabRecord DecodeClosedTab(JsonObject o) => new(
        Text(o["engine"]) ?? "gecko", Text(o["url"]) ?? "", Text(o["title"]) ?? "",
        ReadId(o["workspaceID"]), ReadId(o["groupID"]),
        Bool(o["isPinned"]) ?? false, Bool(o["isMuted"]) ?? false, Int(o["index"]) ?? 0);

    public static JsonNode Encode(ClosedItem item)
    {
        var o = new JsonObject
        {
            ["id"] = Id(item.Id),
            ["closedAt"] = Date(item.ClosedAt),
            ["kind"] = KindName(item.Kind),
            ["tabs"] = new JsonArray([.. item.Tabs.Select(Encode)]),
        };
        if (item.Group is { } group) o["group"] = Encode(group);
        if (item.Workspace is { } workspace) o["workspace"] = Encode(workspace);
        return o;
    }

    public static ClosedItem DecodeClosedItem(JsonObject o) => new()
    {
        Id = ReadId(o["id"]) ?? Guid.NewGuid(),
        ClosedAt = ReadDate(o["closedAt"]) ?? DateTimeOffset.MinValue,
        Kind = Text(o["kind"]) switch { "group" => ClosedItemKind.Group, "workspace" => ClosedItemKind.Workspace, _ => ClosedItemKind.Tab },
        Tabs = List(o["tabs"], DecodeClosedTab),
        Group = o["group"] is JsonObject g ? DecodeGroup(g) : null,
        Workspace = o["workspace"] is JsonObject w ? DecodeWorkspace(w) : null,
    };

    public static string KindName(ClosedItemKind kind) => kind switch
    {
        ClosedItemKind.Group => "group",
        ClosedItemKind.Workspace => "workspace",
        _ => "tab",
    };
}
