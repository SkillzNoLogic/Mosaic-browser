namespace Mosaic.Core.Privacy;

/// <summary>
/// The Public Suffix List (publicsuffix.org), for grouping hosts into sites:
/// <c>news.example.co.uk</c> and <c>shop.example.co.uk</c> both belong to <c>example.co.uk</c>.
/// Mosaic already ships the list for the content blocker.
/// </summary>
/// <remarks>Port of <c>PublicSuffixList</c> in <c>Sources/MosaicCore/Privacy/SiteData.swift</c>.</remarks>
public sealed class PublicSuffixList
{
    private readonly HashSet<string> rules = new(StringComparer.Ordinal);
    private readonly HashSet<string> wildcards = new(StringComparer.Ordinal);
    private readonly HashSet<string> exceptions = new(StringComparer.Ordinal);

    public PublicSuffixList(string text)
    {
        foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var rule = line.Split([' ', '\t'], 2)[0].Trim();
            if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal)) continue;
            var lowered = rule.ToLowerInvariant();
            if (lowered.StartsWith('!')) exceptions.Add(lowered[1..]);
            else if (lowered.StartsWith("*.", StringComparison.Ordinal)) wildcards.Add(lowered[2..]);
            else rules.Add(lowered);
        }
    }

    /// <summary>
    /// The public suffix of <paramref name="host"/> (<c>co.uk</c> for <c>a.example.co.uk</c>);
    /// unknown top-level domains count as suffixes, as the list's algorithm says.
    /// </summary>
    public string PublicSuffix(string host)
    {
        var labels = host.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0) return host;
        for (var start = 0; start < labels.Length; start++)
        {
            var candidate = string.Join('.', labels[start..]);
            if (exceptions.Contains(candidate)) return string.Join('.', labels[(start + 1)..]);
            if (rules.Contains(candidate)) return candidate;
            if (start > 0 && wildcards.Contains(candidate)) return string.Join('.', labels[(start - 1)..]);
        }
        return labels[^1];
    }

    /// <summary>
    /// The site a host belongs to: its public suffix plus one label. An IP address, a
    /// single-label name or a bare suffix is its own site.
    /// </summary>
    public string Site(string host)
    {
        host = host.ToLowerInvariant().TrimStart('.');
        if (host.Length == 0 || !host.Contains('.') || host.All(c => char.IsAsciiDigit(c) || c is '.' or ':')) return host;
        var suffix = PublicSuffix(host);
        if (host.Length <= suffix.Length || !host.EndsWith("." + suffix, StringComparison.Ordinal)) return host;
        var rest = host[..(host.Length - suffix.Length - 1)];
        var label = rest[(rest.LastIndexOf('.') + 1)..];
        return label + "." + suffix;
    }
}

/// <summary>One site that has data stored, as the Cookies and Site Data list shows it.</summary>
public sealed class SiteDataEntry(string site)
{
    public string Site { get; } = site;

    /// <summary>Hosts the data was stored under (<c>example.com</c>, <c>.example.com</c>, <c>www.example.com</c>).</summary>
    public SortedSet<string> Hosts { get; } = new(StringComparer.Ordinal);

    public int ChromiumCookies { get; set; }
    public int GeckoCookies { get; set; }

    /// <summary>Bytes of Gecko storage (local storage, IndexedDB, cache storage…).</summary>
    public long GeckoStorageBytes { get; set; }

    public int CookieCount => ChromiumCookies + GeckoCookies;

    /// <summary>Origins whose storage to clear in Chromium for this site.</summary>
    public IReadOnlyList<string> ChromiumOrigins
    {
        get
        {
            var names = Hosts.Select(host => host.TrimStart('.')).Append(Site).Append("www." + Site)
                .Where(name => name.Length > 0).Distinct().Order(StringComparer.Ordinal);
            return [.. names.SelectMany(name => new[] { $"https://{name}", $"http://{name}" })];
        }
    }

    /// <summary>"Chromium: 2 cookies · Mozilla: 1 cookie, 4 KB", for the list.</summary>
    public string Description
    {
        get
        {
            static string Cookies(int count) => count == 1 ? "1 cookie" : $"{count} cookies";
            List<string> parts = [];
            if (ChromiumCookies > 0) parts.Add($"Chromium: {Cookies(ChromiumCookies)}");
            if (GeckoCookies > 0 || GeckoStorageBytes > 0)
            {
                var gecko = $"Mozilla: {Cookies(GeckoCookies)}";
                if (GeckoStorageBytes > 0) gecko += ", " + ByteCount(GeckoStorageBytes);
                parts.Add(gecko);
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>"512 bytes", "4 KB", "1.2 MB": file sizes in decimal units, as Windows Explorer and macOS show them.</summary>
    public static string ByteCount(long bytes)
    {
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return value < 10 && unit > 0
            ? value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit]
            : Math.Round(value).ToString(System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }
}

public enum SiteDataEngine { Chromium, Gecko }

/// <summary>One cookie or storage record from an engine.</summary>
public sealed record SiteDataRecord(SiteDataEngine Engine, string Host, int Cookies = 0, long StorageBytes = 0);

/// <remarks>Port of <c>SiteDataSummary</c> in <c>Sources/MosaicCore/Privacy/SiteData.swift</c>.</remarks>
public static class SiteDataSummary
{
    /// <summary>Groups engine records by site, sorted by site name.</summary>
    public static IReadOnlyList<SiteDataEntry> Merge(IEnumerable<SiteDataRecord> records, PublicSuffixList? suffixList)
    {
        var bySite = new Dictionary<string, SiteDataEntry>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var host = record.Host.ToLowerInvariant().TrimStart('.');
            if (host.Length == 0) continue;
            var site = suffixList?.Site(host) ?? (host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host);
            if (!bySite.TryGetValue(site, out var entry)) bySite[site] = entry = new SiteDataEntry(site);
            entry.Hosts.Add(record.Host.ToLowerInvariant());
            if (record.Engine == SiteDataEngine.Chromium)
            {
                entry.ChromiumCookies += record.Cookies;
            }
            else
            {
                entry.GeckoCookies += record.Cookies;
                entry.GeckoStorageBytes += record.StorageBytes;
            }
        }
        return [.. bySite.Values.OrderBy(entry => entry.Site, StringComparer.Ordinal)];
    }
}
