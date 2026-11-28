using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Productivity;

/// <summary>One tile on the new-tab page.</summary>
public sealed record SpeedDialSite(Guid Id, string Title, string Url)
{
    public SpeedDialSite(string title, string url) : this(Guid.NewGuid(), title, url) { }

    /// <summary>The host without "www.", used for de-duplication, letter tiles and icons.</summary>
    public string Host => SpeedDial.Host(Url) ?? Url;

    /// <summary>Title, or the host when the user left the title empty.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Host : Title.Trim();
}

public enum SpeedDialError
{
    InvalidAddress,
    Duplicate,
}

public sealed class SpeedDialException(SpeedDialError error) : Exception(Describe(error))
{
    public SpeedDialError Error { get; } = error;

    public static string Describe(SpeedDialError error) => error switch
    {
        SpeedDialError.InvalidAddress => "Enter a web address, like example.com.",
        _ => "That site is already in Speed Dial.",
    };
}

/// <summary>
/// What the new-tab grid shows: the user's pinned sites, then the most visited sites the user has
/// not pinned or removed. Port of <c>Sources/MosaicCore/Productivity/SpeedDial.swift</c>; stored in
/// the same JSON (<c>Productivity\speed-dial.json</c>), so the file moves between platforms.
/// </summary>
public sealed class SpeedDial
{
    private readonly List<SpeedDialSite> pinned;
    private readonly HashSet<string> hiddenTopSites;

    public SpeedDial(IEnumerable<SpeedDialSite>? pinned = null, IEnumerable<string>? hiddenTopSites = null,
        bool showsTopSites = true, bool showsIcons = true)
    {
        this.pinned = (pinned ?? []).ToList();
        this.hiddenTopSites = (hiddenTopSites ?? []).ToHashSet(StringComparer.Ordinal);
        ShowsTopSites = showsTopSites;
        ShowsIcons = showsIcons;
    }

    public IReadOnlyList<SpeedDialSite> Pinned => pinned;

    /// <summary>Normalised addresses (<see cref="Identity"/>) of top sites the user removed from the grid.</summary>
    public IReadOnlySet<string> HiddenTopSites => hiddenTopSites;

    public bool ShowsTopSites { get; set; }

    /// <summary>Fetch site icons (directly from each site, no third party); off shows letter tiles.</summary>
    public bool ShowsIcons { get; set; }

    public SpeedDial Copy() => new(pinned, hiddenTopSites, ShowsTopSites, ShowsIcons);

    // ---- Addresses ----

    /// <summary>
    /// Turns what the user typed into an address: adds https:// to bare hosts, refuses anything
    /// that is not http(s) (no javascript:, file:, data:).
    /// </summary>
    public static string? NormalizedUrl(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("https" or "http") || uri.Host.Length == 0) return null;
        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains('.') && host != "localhost" && uri.IsDefaultPort) return null;
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        var userInfo = uri.UserInfo.Length > 0 ? uri.UserInfo + "@" : "";
        return $"{scheme}://{userInfo}{host}{port}{uri.PathAndQuery}{uri.Fragment}";
    }

    /// <summary>The host of <paramref name="url"/> without "www.".</summary>
    public static string? Host(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>
    /// Key for "same site" comparisons: host without www, path without trailing slash, query kept,
    /// scheme and fragment ignored.
    /// </summary>
    public static string Identity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || Host(url) is not { } host) return url.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        return host + path + uri.Query;
    }

    // ---- Editing pinned sites ----

    public bool Contains(string url)
    {
        var key = Identity(url);
        return pinned.Any(site => Identity(site.Url) == key);
    }

    /// <summary>Pins a site at the end (or at <paramref name="index"/>). Returns the stored site.</summary>
    /// <exception cref="SpeedDialException">Not a web address, or already pinned.</exception>
    public SpeedDialSite Add(string title, string url, int? index = null)
    {
        if (NormalizedUrl(url) is not { } normalized) throw new SpeedDialException(SpeedDialError.InvalidAddress);
        if (Contains(normalized)) throw new SpeedDialException(SpeedDialError.Duplicate);
        var site = new SpeedDialSite((title ?? "").Trim(), normalized);
        pinned.Insert(Math.Clamp(index ?? pinned.Count, 0, pinned.Count), site);
        hiddenTopSites.Remove(Identity(normalized));
        return site;
    }

    /// <exception cref="SpeedDialException">Not a web address, or another pinned site has it.</exception>
    public void Update(Guid id, string title, string url)
    {
        var index = pinned.FindIndex(site => site.Id == id);
        if (index < 0) return;
        if (NormalizedUrl(url) is not { } normalized) throw new SpeedDialException(SpeedDialError.InvalidAddress);
        var key = Identity(normalized);
        if (pinned.Any(site => site.Id != id && Identity(site.Url) == key)) throw new SpeedDialException(SpeedDialError.Duplicate);
        pinned[index] = pinned[index] with { Title = (title ?? "").Trim(), Url = normalized };
    }

    public void Remove(Guid id) => pinned.RemoveAll(site => site.Id == id);

    /// <summary>Moves the site to <paramref name="destination"/> (an index in the list after removal).</summary>
    public void Move(Guid id, int destination)
    {
        var index = pinned.FindIndex(site => site.Id == id);
        if (index < 0) return;
        var site = pinned[index];
        pinned.RemoveAt(index);
        pinned.Insert(Math.Clamp(destination, 0, pinned.Count), site);
    }

    /// <summary>Moves the site one place towards the start (-1) or the end (+1).</summary>
    public void Shift(Guid id, int offset)
    {
        var index = pinned.FindIndex(site => site.Id == id);
        if (index >= 0) Move(id, index + offset);
    }

    // ---- Top sites ----

    public void HideTopSite(string url) => hiddenTopSites.Add(Identity(url));

    public void RestoreHiddenTopSites() => hiddenTopSites.Clear();

    /// <summary>The top sites to show after the pinned ones: not pinned, not hidden, not duplicated, http(s) only, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<SpeedDialSite> VisibleTopSites(IEnumerable<(string Title, string Url)> candidates, int limit)
    {
        if (!ShowsTopSites || limit <= 0) return [];
        var seen = pinned.Select(site => Identity(site.Url)).ToHashSet(StringComparer.Ordinal);
        var result = new List<SpeedDialSite>();
        foreach (var (title, url) in candidates)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var key = Identity(url);
            if (hiddenTopSites.Contains(key) || !seen.Add(key)) continue;
            // A stable id per address so the grid keeps tiles in place between refreshes.
            result.Add(new SpeedDialSite(StableId(key), title, url));
            if (result.Count == limit) break;
        }
        return result;
    }

    /// <summary>A GUID derived from a string (FNV-1a over two seeds), stable across launches; same text as macOS's UUID.</summary>
    internal static Guid StableId(string key)
    {
        static ulong Fnv(ulong seed, string text)
        {
            var hash = seed;
            foreach (var b in Encoding.UTF8.GetBytes(text))
            {
                hash ^= b;
                hash = unchecked(hash * 0x100000001b3UL);
            }
            return hash;
        }
        ulong a = Fnv(0xcbf29ce484222325UL, key), b = Fnv(0x84222325cbf29ce4UL, key);
        var bytes = new byte[16];
        for (var i = 0; i < 8; i++)
        {
            bytes[i] = (byte)(a >> (i * 8));
            bytes[8 + i] = (byte)(b >> (i * 8));
        }
        // Big-endian text like Swift's UUID(uuid:), not .NET's mixed-endian byte constructor.
        return Guid.Parse(Convert.ToHexString(bytes));
    }

    // ---- Stored form ----

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["hiddenTopSites"] = new JsonArray(hiddenTopSites.Order(StringComparer.Ordinal).Select(key => (JsonNode)key).ToArray()),
            ["pinned"] = new JsonArray(pinned.Select(site => (JsonNode)new JsonObject
            {
                ["id"] = site.Id.ToString().ToUpperInvariant(),
                ["title"] = site.Title,
                ["url"] = site.Url,
            }).ToArray()),
            ["showsIcons"] = ShowsIcons,
            ["showsTopSites"] = ShowsTopSites,
        };
        return root.ToJsonString(ProductivityFiles.JsonOptions);
    }

    /// <summary>Reads the stored form; tolerates files written by older versions (missing keys take their defaults). Null when unreadable.</summary>
    public static SpeedDial? FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            var sites = new List<SpeedDialSite>();
            if (root["pinned"] is JsonArray list)
            {
                foreach (var node in list)
                {
                    if (node is not JsonObject item || Text(item["url"]) is not { } url) continue;
                    var id = Guid.TryParse(Text(item["id"]), out var parsed) ? parsed : Guid.NewGuid();
                    sites.Add(new SpeedDialSite(id, Text(item["title"]) ?? "", url));
                }
            }
            var hidden = root["hiddenTopSites"] is JsonArray keys ? keys.Select(key => Text(key)).OfType<string>() : [];
            return new SpeedDial(sites, hidden, Bool(root["showsTopSites"]) ?? true, Bool(root["showsIcons"]) ?? true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
