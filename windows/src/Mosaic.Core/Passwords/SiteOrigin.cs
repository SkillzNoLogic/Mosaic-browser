using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mosaic.Core.Passwords;

/// <summary>
/// A web origin (scheme + host + port), the unit passwords are saved and filled by.
/// Port of <c>Sources/MosaicCore/Passwords/SiteOrigin.swift</c>.
/// </summary>
/// <remarks>
/// Mosaic only fills a password into a page whose origin is <em>exactly</em> the one the password
/// was saved for: <c>https://example.com</c> never fills <c>http://example.com</c>,
/// <c>https://www.example.com</c> or <c>https://example.com:8443</c>. This is stricter than some
/// browsers (which also offer subdomains), on purpose: a subdomain can be run by someone else.
/// Hosts are kept in their ASCII (punycode) form, which is how both engines report origins.
/// </remarks>
[JsonConverter(typeof(SiteOriginJsonConverter))]
public sealed class SiteOrigin : IEquatable<SiteOrigin>, IComparable<SiteOrigin>
{
    private SiteOrigin(string scheme, string host, int? port)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
    }

    /// <summary>"http" or "https".</summary>
    public string Scheme { get; }

    /// <summary>Lowercased host name, IPv4 address or IPv6 address (without brackets).</summary>
    public string Host { get; }

    /// <summary>The port, or null for the scheme's default (80 for http, 443 for https).</summary>
    public int? Port { get; }

    public static SiteOrigin? Create(string scheme, string host, int? port)
    {
        scheme = scheme.ToLowerInvariant();
        host = host.ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']') && host.Length >= 2) host = host[1..^1];
        if (host.EndsWith('.')) host = host[..^1];
        if (scheme is not ("http" or "https") || host.Length == 0
            || host.Any(c => char.IsWhiteSpace(c) || "/?#@\\".Contains(c)))
        {
            return null;
        }
        if (port is { } p && p is < 1 or > 65535) return null;
        return new SiteOrigin(scheme, host, port == DefaultPort(scheme) ? null : port);
    }

    /// <summary>The origin of a page address. Only http and https pages have one here.</summary>
    public static SiteOrigin? FromUrl(string? url)
    {
        var text = url?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        // Only absolute http(s) addresses: anything else ("localhost:8080", "file:", "about:") is not a page origin.
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown) return null;
        string host;
        try
        {
            host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        }
        catch (UriFormatException)
        {
            return null;
        }
        if (host.Length == 0) return null;
        return Create(uri.Scheme, host, uri.IsDefaultPort ? null : uri.Port);
    }

    /// <summary>
    /// What a person types in the "Website" field: "example.com", "https://example.com/login",
    /// "localhost:8080". Without a scheme, https is assumed.
    /// </summary>
    public static SiteOrigin? FromUserInput(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Contains("://", StringComparison.Ordinal) ? FromUrl(text) : FromUrl("https://" + text);
    }

    /// <summary>The serialised origin, as browsers write it: <c>https://example.com</c>, <c>http://127.0.0.1:8000</c>.</summary>
    public string String
    {
        get
        {
            var printedHost = Host.Contains(':') ? $"[{Host}]" : Host;
            return Port is { } port ? $"{Scheme}://{printedHost}:{port}" : $"{Scheme}://{printedHost}";
        }
    }

    /// <summary>
    /// Short form for lists and prompts: the host, plus the port when it is not the default.
    /// The scheme is left out for https and shown for http, so an unencrypted site stands out.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var printedHost = Host.Contains(':') ? $"[{Host}]" : Host;
            var hostPort = Port is { } port ? $"{printedHost}:{port}" : printedHost;
            return Scheme == "https" ? hostPort : $"http://{hostPort}";
        }
    }

    public bool IsSecure => Scheme == "https";

    /// <summary>The port actually used, defaults included.</summary>
    public int EffectivePort => Port ?? DefaultPort(Scheme);

    public static int DefaultPort(string scheme) => scheme == "http" ? 80 : 443;

    public override string ToString() => String;

    public bool Equals(SiteOrigin? other) =>
        other is not null && Scheme == other.Scheme && Host == other.Host && Port == other.Port;

    public override bool Equals(object? obj) => Equals(obj as SiteOrigin);

    public override int GetHashCode() => HashCode.Combine(Scheme, Host, Port);

    public int CompareTo(SiteOrigin? other)
    {
        if (other is null) return 1;
        var byHost = string.CompareOrdinal(Host, other.Host);
        if (byHost != 0) return byHost;
        var byScheme = string.CompareOrdinal(Scheme, other.Scheme);
        return byScheme != 0 ? byScheme : EffectivePort.CompareTo(other.EffectivePort);
    }

    public static bool operator ==(SiteOrigin? left, SiteOrigin? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(SiteOrigin? left, SiteOrigin? right) => !(left == right);
}

/// <summary>Codable as the serialised string, so stored lists stay readable (same as macOS).</summary>
public sealed class SiteOriginJsonConverter : JsonConverter<SiteOrigin>
{
    public override SiteOrigin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return SiteOrigin.FromUrl(text) ?? throw new JsonException($"not an origin: {text}");
    }

    public override void Write(Utf8JsonWriter writer, SiteOrigin value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.String);
}
