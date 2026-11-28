namespace Mosaic.Core.Privacy;

/// <summary>
/// Query parameters that only exist to track people across sites, and their removal from
/// addresses before they are loaded.
/// </summary>
/// <remarks>
/// <para>Port of <c>Sources/MosaicCore/Privacy/TrackingParameters.swift</c>. Sources:</para>
/// <list type="bullet">
/// <item>Firefox's query-stripping list, the Remote Settings collection <c>main/query-stripping</c>
/// that Firefox's own "strip tracking parameters" feature uses (snapshot taken 2026-11-27):
/// https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/query-stripping/records</item>
/// <item>Google Analytics' campaign parameters (<c>utm_*</c>), which Firefox does not strip by
/// default but Brave and AdGuard's URL Tracking filter do. They are matched by prefix here and
/// listed by name for Gecko, whose pref takes exact names only.</item>
/// </list>
/// <para>Firefox's allow-list (<c>googleadservices.com</c>, whose redirects need <c>gclid</c>) is honoured the same way.</para>
/// </remarks>
public static class TrackingParameters
{
    /// <summary>Firefox's list, exactly as published.</summary>
    public static IReadOnlyList<string> FirefoxList { get; } =
    [
        "gclid", "dclid", "msclkid", "_openstat", "yclid", "wickedid", "twclid", "_hsenc", "__hssc", "__hstc",
        "__hsfp", "hsctatracking", "wbraid", "gbraid", "ysclid", "mc_eid", "oly_anon_id", "oly_enc_id", "__s",
        "vero_id", "mkt_tok", "fbclid",
    ];

    /// <summary>Campaign parameters, by name (for engines that cannot match a prefix).</summary>
    public static IReadOnlyList<string> CampaignParameters { get; } =
    [
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_name",
        "utm_source_platform", "utm_creative_format", "utm_marketing_tactic", "utm_cid", "utm_reader",
        "utm_referrer", "utm_social", "utm_social-type", "utm_brand", "utm_place", "utm_pubreferrer", "utm_swu",
        "utm_viz_id",
    ];

    /// <summary>Every exact name, lower-cased; what Gecko's <c>privacy.query_stripping.strip_list</c> gets.</summary>
    public static IReadOnlyList<string> AllNames { get; } = [.. FirefoxList, .. CampaignParameters];

    /// <summary>Sites whose own links need the parameters (Firefox's allow-list).</summary>
    public static IReadOnlySet<string> AllowedSites { get; } = new HashSet<string>(StringComparer.Ordinal) { "googleadservices.com" };

    private static readonly HashSet<string> Names = new(AllNames, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="name"/> is a tracking parameter (case-insensitive; any <c>utm_</c> name counts).</summary>
    public static bool IsTracking(string name)
    {
        var lowered = name.ToLowerInvariant();
        return Names.Contains(lowered) || lowered.StartsWith("utm_", StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="url"/> without its tracking parameters, or null when it has none (or is not
    /// a web address, or belongs to an allowed site). Everything else about the address is kept
    /// character for character, including the order and spelling of other parameters and the fragment.
    /// </summary>
    public static string? Stripped(string url)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0) return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // A '#' before the '?' means the '?' is part of the fragment.
        var hash = url.IndexOf('#');
        if (hash >= 0 && hash < queryStart) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            var host = parsed.Host.ToLowerInvariant();
            if (AllowedSites.Any(site => host == site || host.EndsWith("." + site, StringComparison.Ordinal))) return null;
        }

        var fragmentStart = hash >= 0 ? hash : url.Length;
        var query = url[(queryStart + 1)..fragmentStart];
        var pairs = query.Split('&');
        var kept = pairs.Where(pair =>
        {
            var name = pair.Split('=', 2)[0];
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(name);
            }
            catch (UriFormatException)
            {
                decoded = name;
            }
            return !IsTracking(decoded);
        }).ToList();
        if (kept.Count == pairs.Length) return null;
        var start = url[..queryStart];
        var fragment = url[fragmentStart..];
        return kept.Count == 0 ? start + fragment : start + "?" + string.Join('&', kept) + fragment;
    }
}
