using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Permissions;

/// <summary>A permission Mosaic remembers per site. The JSON names are the lower-case member names.</summary>
public enum SitePermission { Camera, Microphone, Location, Notifications, Popups, Autoplay, Clipboard }

public enum SitePermissionDecision { Allow, Block }

/// <summary>One remembered decision.</summary>
public sealed record SitePermissionEntry(string Site, SitePermission Permission, SitePermissionDecision Decision);

/// <summary>A decision was set (<see cref="Decision"/> non-null) or forgotten (null).</summary>
public sealed record SitePermissionChange(string Site, SitePermission Permission, SitePermissionDecision? Decision);

/// <summary>
/// Remembered per-site permission decisions (camera, microphone, location…). Port of
/// <c>Sources/MosaicCore/Permissions/SitePermissionStore.swift</c>, same file format
/// (<c>SitePermissions.json</c>), so a profile moves between macOS and Windows.
/// </summary>
/// <remarks>
/// <para>Owner: <b>engine</b> (it answers the engines' permission requests and records the
/// answers); the <b>privacy</b> area's site-permissions page lists and edits them through this
/// same object (<c>BrowserModel.SitePermissions</c>).</para>
/// <para>Decisions are kept per <i>site</i>, which is a web origin: <c>https://example.com</c>,
/// <c>http://localhost:8000</c> (scheme, host and a non-default port, lower case). Both engines
/// scope permissions to origins, so one decision applies to Chromium and Gecko tabs alike.</para>
/// <para>Safe to use from any thread. The file is read once, when the store is created, and
/// written in the background after each change.</para>
/// </remarks>
public sealed class SitePermissionStore
{
    private readonly object gate = new();
    private readonly BackgroundFile file;
    /// <summary>site → permission → decision.</summary>
    private readonly Dictionary<string, Dictionary<SitePermission, SitePermissionDecision>> decisions = [];

    public SitePermissionStore(string filePath)
    {
        FilePath = filePath;
        file = new BackgroundFile(filePath);
        Load();
    }

    public string FilePath { get; }

    /// <summary>
    /// Raised on the thread that made the change, after it is in memory, whenever a decision is
    /// added, changed or removed (not when a call changes nothing).
    /// </summary>
    public event Action<SitePermissionChange>? Changed;

    /// <summary>The remembered decision, or null to ask. <paramref name="site"/> may be an origin or any URL on it.</summary>
    public SitePermissionDecision? Decision(SitePermission permission, string site)
    {
        if (Site(site) is not { } key) return null;
        lock (gate)
        {
            return decisions.TryGetValue(key, out var permissions) && permissions.TryGetValue(permission, out var decision)
                ? decision
                : null;
        }
    }

    /// <summary>
    /// Remembers (or, with null, forgets) a decision. <paramref name="site"/> may be an origin or
    /// any URL on it; values that are not web sites are ignored.
    /// </summary>
    public void Set(SitePermissionDecision? decision, SitePermission permission, string site)
    {
        if (Site(site) is not { } key) return;
        lock (gate)
        {
            decisions.TryGetValue(key, out var permissions);
            SitePermissionDecision? previous = permissions is not null && permissions.TryGetValue(permission, out var old) ? old : null;
            if (previous == decision) return;
            if (decision is { } value)
            {
                if (permissions is null) decisions[key] = permissions = [];
                permissions[permission] = value;
            }
            else if (permissions is not null)
            {
                permissions.Remove(permission);
                if (permissions.Count == 0) decisions.Remove(key);
            }
            // Queued under the lock so concurrent changes reach the file in order.
            file.Write(Serialize(EntriesLocked()));
        }
        Changed?.Invoke(new SitePermissionChange(key, permission, decision));
    }

    /// <summary>Every remembered decision, sorted by site, then in the order permissions are declared.</summary>
    public IReadOnlyList<SitePermissionEntry> Entries()
    {
        lock (gate) return EntriesLocked();
    }

    /// <summary>Forgets every decision for <paramref name="site"/> (an origin or any URL on it).</summary>
    public void RemoveAll(string site)
    {
        if (Site(site) is not { } key) return;
        List<SitePermission> permissions;
        lock (gate) permissions = decisions.TryGetValue(key, out var found) ? [.. found.Keys] : [];
        foreach (var permission in permissions) Set(null, permission, key);
    }

    /// <summary>Waits until every change so far is on disk.</summary>
    public Task FlushAsync() => file.FlushAsync();

    // ---- Sites ----

    /// <summary>
    /// The site (web origin) a URL belongs to, or null if it is not on the web. Accepts a full URL,
    /// an origin, or a bare host name (taken as <c>https</c>). <c>file:</c> URLs share the single
    /// site <c>file://</c>, as they do in both engines.
    /// </summary>
    public static string? Site(string? url)
    {
        var trimmed = url?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        var text = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return null;
        var scheme = parsed.Scheme.ToLowerInvariant();
        if (scheme == "file") return "file://";
        if (scheme is not ("http" or "https")) return null;
        var host = parsed.HostNameType == UriHostNameType.IPv6 ? parsed.Host : parsed.IdnHost;
        host = host.ToLowerInvariant();
        if (host.Length == 0 || host.Contains(' ')) return null;
        if (parsed.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('[')) host = $"[{host}]";
        return parsed.IsDefaultPort
            ? $"{scheme}://{host}"
            : $"{scheme}://{host}:{parsed.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The site as shown to people: the host without <c>https://</c> (other schemes are kept, so
    /// an insecure <c>http://</c> site stays recognisable).
    /// </summary>
    public static string DisplayName(string site)
    {
        if (site == "file://") return "Local files";
        return site.StartsWith("https://", StringComparison.Ordinal) ? site["https://".Length..] : site;
    }

    public static string Name(SitePermission permission) => permission.ToString().ToLowerInvariant();

    public static SitePermission? ParsePermission(string? name) =>
        Enum.TryParse<SitePermission>(name, ignoreCase: true, out var value) && Name(value) == name?.ToLowerInvariant() ? value : null;

    public static string Name(SitePermissionDecision decision) => decision == SitePermissionDecision.Allow ? "allow" : "block";

    public static SitePermissionDecision? ParseDecision(string? name) => name switch
    {
        "allow" => SitePermissionDecision.Allow,
        "block" => SitePermissionDecision.Block,
        _ => null,
    };

    // ---- Persistence: {"entries": [{"decision", "permission", "site"}], "version": 1} ----

    private List<SitePermissionEntry> EntriesLocked() =>
    [
        .. decisions
            .SelectMany(site => site.Value.Select(pair => new SitePermissionEntry(site.Key, pair.Key, pair.Value)))
            .OrderBy(entry => entry.Site, StringComparer.Ordinal)
            .ThenBy(entry => entry.Permission),
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Serialize(IEnumerable<SitePermissionEntry> entries)
    {
        var list = new JsonArray();
        foreach (var entry in entries)
        {
            // Keys in sorted order, as the macOS encoder writes them.
            list.Add(new JsonObject
            {
                ["decision"] = Name(entry.Decision),
                ["permission"] = Name(entry.Permission),
                ["site"] = entry.Site,
            });
        }
        return new JsonObject { ["entries"] = list, ["version"] = 1 }.ToJsonString(Indented);
    }

    private void Load()
    {
        JsonNode? root;
        try
        {
            if (!File.Exists(FilePath)) return;
            root = JsonNode.Parse(File.ReadAllText(FilePath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return;
        }
        if (root?["entries"] is not JsonArray entries) return;
        foreach (var node in entries)
        {
            if (node is not JsonObject entry) continue;
            if (Site(Text(entry["site"])) is not { } site
                || ParsePermission(Text(entry["permission"])) is not { } permission
                || ParseDecision(Text(entry["decision"])) is not { } decision)
            {
                continue;
            }
            if (!decisions.TryGetValue(site, out var permissions)) decisions[site] = permissions = [];
            permissions[permission] = decision;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
