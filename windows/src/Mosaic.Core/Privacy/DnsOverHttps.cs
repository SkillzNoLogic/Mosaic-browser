using System.Text.Json.Serialization;

namespace Mosaic.Core.Privacy;

/// <summary>DNS over HTTPS: resolving names through an encrypted resolver instead of the network's.</summary>
/// <remarks>Port of <c>Sources/MosaicCore/Privacy/DNSOverHTTPS.swift</c>, plus WebView2's form of the setting.</remarks>
public static class DnsOverHttps
{
    /// <summary>A public resolver. <c>Template</c> is its RFC 8484 URI template, as both engines take it.</summary>
    public sealed record Provider(string Id, string Name, string Template, string Note);

    /// <summary>Well-known public resolvers with no-logging policies.</summary>
    public static IReadOnlyList<Provider> Providers { get; } =
    [
        new("cloudflare", "Cloudflare", "https://cloudflare-dns.com/dns-query", "1.1.1.1. Fast; keeps minimal logs for 24 hours."),
        new("quad9", "Quad9", "https://dns.quad9.net/dns-query", "Swiss non-profit. Blocks known malicious domains."),
        new("mullvad", "Mullvad", "https://dns.mullvad.net/dns-query", "No logging. No filtering."),
        new("nextdns", "NextDNS", "https://dns.nextdns.io", "Configurable filtering with an account."),
        new("google", "Google Public DNS", "https://dns.google/dns-query", "Large and fast; logs are kept for a short time."),
    ];

    public const string CustomProviderId = "custom";

    /// <summary>An https URL with a host, optionally with an RFC 6570 <c>{?dns}</c> suffix.</summary>
    public static bool IsValidTemplate(string text)
    {
        var value = text.Trim().Replace("{?dns}", "", StringComparison.Ordinal);
        return value.Length > 0 && !value.Contains(' ', StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host.Length > 0;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<DnsMode>))]
public enum DnsMode
{
    /// <summary>The system's resolver.</summary>
    [JsonStringEnumMemberName("off")] Off,
    /// <summary>Try the encrypted resolver, fall back to the system's when it fails.</summary>
    [JsonStringEnumMemberName("automatic")] Automatic,
    /// <summary>Only the encrypted resolver; names fail to resolve without it.</summary>
    [JsonStringEnumMemberName("strict")] Strict,
}

public static class DnsModes
{
    public static string Id(this DnsMode mode) => mode switch
    {
        DnsMode.Automatic => "automatic",
        DnsMode.Strict => "strict",
        _ => "off",
    };

    public static DnsMode? FromId(string id) => id switch
    {
        "off" => DnsMode.Off,
        "automatic" => DnsMode.Automatic,
        "strict" => DnsMode.Strict,
        _ => null,
    };
}

/// <summary>The user's DNS-over-HTTPS choice. JSON keys as macOS's <c>DNSOverHTTPS.Setting</c>.</summary>
public sealed record DnsOverHttpsSetting
{
    [JsonPropertyName("mode")] public DnsMode Mode { get; init; } = DnsMode.Off;

    /// <summary>A provider's <see cref="DnsOverHttps.Provider.Id"/>, or "custom".</summary>
    [JsonPropertyName("providerID")] public string ProviderId { get; init; } = "cloudflare";

    [JsonPropertyName("customTemplate")] public string CustomTemplate { get; init; } = "";

    /// <summary>The template in effect; null when off, or when a custom one is not valid.</summary>
    [JsonIgnore]
    public string? Template
    {
        get
        {
            if (Mode == DnsMode.Off) return null;
            if (ProviderId == DnsOverHttps.CustomProviderId)
            {
                return DnsOverHttps.IsValidTemplate(CustomTemplate) ? CustomTemplate.Trim() : null;
            }
            return DnsOverHttps.Providers.FirstOrDefault(provider => provider.Id == ProviderId)?.Template;
        }
    }

    /// <summary>
    /// Gecko's <c>network.trr.*</c> preferences. TRR mode 2 = encrypted first, 3 = encrypted only,
    /// 5 = off by choice. Values are <see cref="int"/> or <see cref="string"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, object> GeckoPreferences => Template is not { } template
        ? new Dictionary<string, object> { ["network.trr.mode"] = 5 }
        : new Dictionary<string, object>
        {
            ["network.trr.mode"] = Mode == DnsMode.Strict ? 3 : 2,
            ["network.trr.uri"] = template,
            ["network.trr.custom_uri"] = template,
        };

    /// <summary>Chromium's browser-wide (local state) preferences, as macOS sets them through CEF.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> ChromiumPreferences => Template is not { } template
        ? new Dictionary<string, string> { ["dns_over_https.mode"] = "off", ["dns_over_https.templates"] = "" }
        : new Dictionary<string, string>
        {
            ["dns_over_https.mode"] = Mode == DnsMode.Strict ? "secure" : "automatic",
            ["dns_over_https.templates"] = template,
        };

    /// <summary>
    /// The Chromium feature (an entry for <c>--enable-features=</c>) that gives WebView2 this
    /// setting, or null when off. WebView2 has
    /// no API for Chromium's DNS preferences, so Mosaic passes Chromium's own <c>DnsOverHttps</c>
    /// feature with its <c>Templates</c> and <c>Fallback</c> parameters (the configuration
    /// Chromium's stub resolver reads when no policy or preference sets one) in
    /// <c>CoreWebView2EnvironmentOptions.AdditionalBrowserArguments</c>. Parameter values are
    /// percent-escaped because '/' and ':' separate the parameters themselves.
    /// Takes effect when the WebView2 environment is next created (at Mosaic's next start).
    /// Chromium reads only the last <c>--enable-features</c> switch, so whoever builds the
    /// arguments merges this entry into one list (see <see cref="BrowserArguments.Merge"/>).
    /// </summary>
    [JsonIgnore]
    public string? WebView2Feature => Template is not { } template
        ? null
        : $"DnsOverHttps:Fallback/{(Mode == DnsMode.Strict ? "false" : "true")}/Templates/{Uri.EscapeDataString(template)}";
}
