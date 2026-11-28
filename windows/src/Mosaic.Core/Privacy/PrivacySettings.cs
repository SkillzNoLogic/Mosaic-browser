using System.Text.Json;
using System.Text.Json.Serialization;
using Mosaic.Core.Blocker;

namespace Mosaic.Core.Privacy;

/// <summary>
/// The user's privacy and network-security choices (Settings › Privacy &amp; Security), apart from
/// the content blocker's own (<see cref="BlockerSettings"/>, <see cref="BlockerOptions"/>).
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Privacy/PrivacySettings.swift</c>. Immutable, because the
/// navigation policy reads it from WebView2's event handlers while the settings page replaces it.
/// Its JSON is macOS's Codable form (same keys), and decoding is tolerant: settings saved by an
/// older Mosaic lack newer keys and get their defaults.
/// </remarks>
public sealed record PrivacySettings
{
    /// <summary>Load every page over HTTPS; plain-HTTP pages only after the user agrees.</summary>
    [JsonPropertyName("httpsOnly")] public bool HttpsOnly { get; init; }

    /// <summary>Sites (normalised hostnames, subdomains included) that may load over HTTP. Sorted.</summary>
    [JsonPropertyName("httpsExceptions")] public IReadOnlyList<string> HttpsExceptions { get; init; } = [];

    /// <summary>Remove tracking parameters (<c>fbclid</c>, <c>utm_source</c>, …) from addresses.</summary>
    [JsonPropertyName("stripTrackingParameters")] public bool StripTrackingParameters { get; init; } = true;

    /// <summary>Refuse cookies and storage for third-party frames and requests.</summary>
    [JsonPropertyName("blockThirdPartyCookies")] public bool BlockThirdPartyCookies { get; init; } = true;

    /// <summary>Delete cookies and site data when Mosaic quits.</summary>
    [JsonPropertyName("clearCookiesOnExit")] public bool ClearCookiesOnExit { get; init; }

    [JsonPropertyName("dnsOverHTTPS")] public DnsOverHttpsSetting DnsOverHttps { get; init; } = new();

    /// <summary>Whether <paramref name="host"/> (or a parent domain of it) is in <see cref="HttpsExceptions"/>.</summary>
    public bool AllowsHttp(string host) =>
        BlockerSettings.Normalize(host) is { } site && Covers(HttpsExceptions, site);

    /// <summary>A copy with <paramref name="site"/> (a hostname or URL) allowed over HTTP, or no longer allowed.</summary>
    public PrivacySettings WithHttpException(bool allowed, string site)
    {
        if (BlockerSettings.Normalize(site) is not { } normalized) return this;
        var sites = HttpsExceptions.Where(existing => existing != normalized).ToList();
        if (allowed) sites.Add(normalized);
        sites.Sort(StringComparer.Ordinal);
        return this with { HttpsExceptions = sites };
    }

    /// <summary>Whether <paramref name="sites"/> has <paramref name="site"/> or a parent domain of it.</summary>
    internal static bool Covers(IEnumerable<string> sites, string site) =>
        sites.Any(entry => site == entry || site.EndsWith("." + entry, StringComparison.Ordinal));

    public bool Equals(PrivacySettings? other) =>
        other is not null && HttpsOnly == other.HttpsOnly && HttpsExceptions.SequenceEqual(other.HttpsExceptions)
        && StripTrackingParameters == other.StripTrackingParameters && BlockThirdPartyCookies == other.BlockThirdPartyCookies
        && ClearCookiesOnExit == other.ClearCookiesOnExit && DnsOverHttps == other.DnsOverHttps;

    public override int GetHashCode() =>
        HashCode.Combine(HttpsOnly, HttpsExceptions.Count, StripTrackingParameters, BlockThirdPartyCookies, ClearCookiesOnExit, DnsOverHttps);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Settings from their JSON; defaults for anything missing, and for text that is not valid.</summary>
    public static PrivacySettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<PrivacySettings>(json, JsonOptions) ?? new();
            // A null in the file (or a type that changed) must not leave a null behind.
            return settings with
            {
                HttpsExceptions = [.. (settings.HttpsExceptions ?? []).Select(BlockerSettings.Normalize).OfType<string>().Distinct().Order(StringComparer.Ordinal)],
                DnsOverHttps = settings.DnsOverHttps ?? new(),
            };
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

/// <summary>
/// What to do with a top-level navigation before it is loaded: the HTTPS-only upgrade and
/// tracking-parameter removal, shared by both engines' code paths.
/// </summary>
/// <remarks>Port of <c>NavigationPolicy</c> in <c>Sources/MosaicCore/Privacy/PrivacySettings.swift</c>.</remarks>
public static class NavigationPolicy
{
    /// <summary>The address to load instead of <paramref name="url"/>, or null to load it as it is.</summary>
    /// <param name="url">The address about to be loaded.</param>
    /// <param name="settings">The user's choices.</param>
    /// <param name="sessionExceptions">Hosts the user let through over HTTP this session.</param>
    /// <param name="allowInsecure">The user chose "Continue to HTTP Site" for this load.</param>
    public static string? Rewrite(string url, PrivacySettings settings, IReadOnlySet<string>? sessionExceptions = null, bool allowInsecure = false)
    {
        var result = url;
        if (settings.StripTrackingParameters && TrackingParameters.Stripped(result) is { } stripped) result = stripped;
        if (settings.HttpsOnly && !allowInsecure && HttpsUpgrade(result, settings, sessionExceptions) is { } upgraded) result = upgraded;
        return result == url ? null : result;
    }

    /// <summary>
    /// <c>https://</c> for an <c>http://</c> address, unless the host is exempt. Local addresses
    /// (localhost, <c>.local</c>, private IPv4 ranges, single-label names) stay on HTTP: they rarely
    /// have certificates, and their traffic does not leave the machine or network (Firefox exempts
    /// them the same way). Everything after the scheme is kept as it is, apart from a <c>:80</c> port.
    /// </summary>
    public static string? HttpsUpgrade(string url, PrivacySettings settings, IReadOnlySet<string>? sessionExceptions = null)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url[7..];
        var authorityEnd = rest.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        var path = authorityEnd < 0 ? "" : rest[authorityEnd..];
        var hostPort = authority[(authority.LastIndexOf('@') + 1)..];
        var userInfo = authority[..(authority.Length - hostPort.Length)];
        string host, port;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0) return null;
            host = hostPort[..(close + 1)];
            port = hostPort[(close + 1)..];
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            host = colon < 0 ? hostPort : hostPort[..colon];
            port = colon < 0 ? "" : hostPort[colon..];
        }
        var spelledHost = host;
        host = host.ToLowerInvariant();
        if (host.Length == 0 || IsLocal(host) || settings.AllowsHttp(host)) return null;
        if (BlockerSettings.Normalize(host) is { } site && sessionExceptions is not null && PrivacySettings.Covers(sessionExceptions, site)) return null;
        if (port == ":80") port = "";
        return "https://" + userInfo + spelledHost + port + path;
    }

    /// <summary>Whether <paramref name="host"/> is this machine or the local network.</summary>
    public static bool IsLocal(string host)
    {
        host = host.ToLowerInvariant().Trim('[', ']');
        if (host is "localhost" or "::1" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".home.arpa", StringComparison.Ordinal) || !host.Contains('.'))
        {
            return true;
        }
        var parts = host.Split('.');
        if (parts.Length != 4 || !host.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
        var octets = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], out octets[i])) return false;
        }
        return (octets[0], octets[1]) switch
        {
            (10, _) or (127, _) or (192, 168) or (169, 254) => true,
            (172, >= 16 and <= 31) => true,
            _ => false,
        };
    }
}

/// <summary>How the page in a tab reached Mosaic, for the address field's padlock.</summary>
public enum ConnectionSecurity
{
    /// <summary>Not a web page: the start page, Mosaic's own pages, <c>about:</c>, files.</summary>
    NotApplicable,
    /// <summary>HTTPS.</summary>
    Secure,
    /// <summary>Plain HTTP from the internet: anyone on the network can read or change it.</summary>
    Insecure,
    /// <summary>Plain HTTP to this PC or the local network (localhost, 192.168.x.x, .local).</summary>
    Local,
}

public static class ConnectionSecurities
{
    /// <summary>The security of a page at <paramref name="url"/> (empty for the start page).</summary>
    public static ConnectionSecurity Of(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return ConnectionSecurity.NotApplicable;
        return parsed.Scheme.ToLowerInvariant() switch
        {
            "https" or "wss" => ConnectionSecurity.Secure,
            "http" or "ws" => NavigationPolicy.IsLocal(parsed.Host) ? ConnectionSecurity.Local : ConnectionSecurity.Insecure,
            _ => ConnectionSecurity.NotApplicable,
        };
    }

    /// <summary>macOS's raw value: "secure", "insecure", "local", "notApplicable" (automation <c>dump</c>).</summary>
    public static string Id(this ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.Secure => "secure",
        ConnectionSecurity.Insecure => "insecure",
        ConnectionSecurity.Local => "local",
        _ => "notApplicable",
    };

    /// <summary>The padlock's tooltip and accessible name; empty when there is no padlock.</summary>
    public static string Label(this ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.Secure => "Secure connection",
        ConnectionSecurity.Insecure => "Not secure: this page was loaded over plain HTTP",
        ConnectionSecurity.Local => "Local network page (not encrypted)",
        _ => "",
    };

    /// <summary>
    /// The icon name for the system area's <c>Icons.Make</c> (macOS's SF Symbol names, which it
    /// maps to Fluent glyphs), or null for no padlock.
    /// </summary>
    public static string? IconName(this ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.Secure => "lock.fill",
        ConnectionSecurity.Insecure => "lock.open.trianglebadge.exclamationmark",
        ConnectionSecurity.Local => "lock.open",
        _ => null,
    };
}
