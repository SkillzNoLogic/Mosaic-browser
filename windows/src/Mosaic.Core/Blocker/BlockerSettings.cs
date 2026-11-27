namespace Mosaic.Core.Blocker;

/// <summary>
/// The user's content-blocking choices: the master switch and the sites the blocker is turned off for.
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Blocker/BlockerSettings.swift</c>. Immutable, because
/// <see cref="RequestFilter"/> reads it on every request while the UI thread replaces it: a
/// change is a new instance (<see cref="WithEnabled"/>, <see cref="WithAllowlisted"/>), so a
/// reader can never see one half-updated.
/// </remarks>
public sealed class BlockerSettings
{
    private readonly HashSet<string> allowlist;

    /// <param name="isEnabled">Blocking is on unless the user turns it off.</param>
    /// <param name="allowlist">Sites or URLs to exempt; anything without a host is ignored.</param>
    public BlockerSettings(bool isEnabled = true, IEnumerable<string>? allowlist = null)
    {
        IsEnabled = isEnabled;
        this.allowlist = new HashSet<string>(
            (allowlist ?? []).Select(Normalize).OfType<string>(), StringComparer.Ordinal);
    }

    public bool IsEnabled { get; }

    /// <summary>
    /// Sites (normalised hostnames) where blocking is off. An entry covers the host and all of its subdomains.
    /// </summary>
    public IReadOnlySet<string> Allowlist => allowlist;

    /// <summary>
    /// The form a site is stored in: lower-case hostname without a leading <c>www.</c>. Accepts a
    /// hostname or a URL; null for anything without a host.
    /// </summary>
    public static string? Normalize(string site)
    {
        var host = site.Trim().ToLowerInvariant();
        if (host.Length == 0) return null;
        if (host.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(host, UriKind.Absolute, out var parsed) || parsed.IdnHost.Length == 0) return null;
            host = parsed.IdnHost;
        }
        else
        {
            var end = host.IndexOfAny(['/', '?', '#']);
            if (end >= 0) host = host[..end];
        }

        // "host:8080" → "host"; any other colon ("about:blank") means it is not a host.
        var colon = host.LastIndexOf(':');
        if (colon >= 0 && !host.AsSpan(colon + 1).ContainsAnyExceptInRange('0', '9')) host = host[..colon];
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host.Length == 0 || host.Contains(' ') || host.Contains(':') ? null : host;
    }

    /// <summary>
    /// The site a page belongs to for allowlisting; null for pages that are not on the web
    /// (<c>about:</c>, <c>file:</c>, extension pages), which are never filtered.
    /// </summary>
    public static string? SiteOfPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return null;
        return Normalize(parsed.IdnHost);
    }

    public bool IsAllowlisted(string site)
    {
        // An entry also covers subdomains: "example.com" covers "news.example.com".
        for (var rest = site; ; rest = rest[(rest.IndexOf('.') + 1)..])
        {
            if (allowlist.Contains(rest)) return true;
            if (!rest.Contains('.')) return false;
        }
    }

    /// <summary>Whether requests made by the page at <paramref name="pageUrl"/> should be put to the engine at all.</summary>
    public bool Filters(string pageUrl) =>
        IsEnabled && SiteOfPage(pageUrl) is { } site && !IsAllowlisted(site);

    public BlockerSettings WithEnabled(bool enabled) => enabled == IsEnabled ? this : new(enabled, allowlist);

    /// <summary>
    /// A copy with <paramref name="site"/> (a hostname or URL) exempted or no longer exempted; this
    /// instance if <paramref name="site"/> has no host.
    /// </summary>
    public BlockerSettings WithAllowlisted(bool allowlisted, string site)
    {
        if (Normalize(site) is not { } normalized) return this;
        var sites = new HashSet<string>(allowlist, StringComparer.Ordinal);
        if (allowlisted) sites.Add(normalized);
        else sites.Remove(normalized);
        return new BlockerSettings(IsEnabled, sites);
    }
}
