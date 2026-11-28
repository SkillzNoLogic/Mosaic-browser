using System.Globalization;
using System.Text;

namespace Mosaic.Core.Tabs;

/// <summary>
/// Decides whether two addresses show the same page, for duplicate-tab detection.
/// Port of <c>URLIdentity</c> (<c>Sources/MosaicCore/Tabs/TabMatching.swift</c>).
/// </summary>
public static class UrlIdentity
{
    /// <summary>
    /// A key that is equal for addresses Mosaic treats as the same page, or null for addresses
    /// that never count as duplicates (blank, about:, data:, Mosaic's own pages). Web addresses
    /// ignore: http vs https, a leading "www.", letter case in the host, the default port, the
    /// fragment, and a trailing slash. The query is significant.
    /// </summary>
    public static string? Key(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.Length == 0) return null;
        var colon = trimmed.IndexOf(':');
        if (colon <= 0) return null;
        var scheme = trimmed[..colon].ToLowerInvariant();
        switch (scheme)
        {
            case "http":
            case "https":
                if (!trimmed[(colon + 1)..].StartsWith("//", StringComparison.Ordinal)) return null;
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
                var host = uri.Host.ToLowerInvariant();
                if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
                var key = new StringBuilder(host);
                if (!uri.IsDefaultPort && uri.Port is not (80 or 443)) key.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
                var path = uri.AbsolutePath;
                while (path.Length > 1 && path.EndsWith('/')) path = path[..^1];
                if (path.Length == 0) path = "/";
                key.Append(path);
                if (uri.Query.Length > 1) key.Append(uri.Query);
                return key.ToString();
            case "file":
                return Uri.TryCreate(trimmed, UriKind.Absolute, out var file) ? "file:" + file.AbsolutePath : null;
            default:
                return null;
        }
    }

    public static bool SamePage(string a, string b) => Key(a) is { } keyA && keyA == Key(b);
}

public static class DuplicateFinder
{
    /// <summary>The first candidate showing the same page as <paramref name="address"/>, skipping <paramref name="excluding"/>.</summary>
    public static Guid? FirstMatch(string address, IEnumerable<(Guid Id, string Url)> candidates, Guid? excluding = null)
    {
        if (UrlIdentity.Key(address) is not { } key) return null;
        foreach (var (id, url) in candidates)
        {
            if (id != excluding && UrlIdentity.Key(url) == key) return id;
        }
        return null;
    }
}

/// <summary>Type-to-filter ranking for the tab search panel. Port of <c>TabSearch</c>.</summary>
public static class TabSearch
{
    /// <summary>
    /// How well a tab matches <paramref name="query"/>, higher is better; null when it does not
    /// match. Every word of the query has to appear in the title or the address (case and accents
    /// ignored). An empty query matches everything with score 0.
    /// </summary>
    public static int? Score(string query, string title, string url)
    {
        var terms = Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return 0;
        var foldedTitle = Fold(title);
        var address = Fold(StrippingScheme(url));
        var slash = address.IndexOf('/');
        var host = slash >= 0 ? address[..slash] : address;
        var words = SplitWords(foldedTitle);
        var total = 0;
        foreach (var term in terms)
        {
            if (foldedTitle.StartsWith(term, StringComparison.Ordinal)) total += 100;
            else if (words.Any(word => word.StartsWith(term, StringComparison.Ordinal))) total += 60;
            else if (host.StartsWith(term, StringComparison.Ordinal) || host.StartsWith("www." + term, StringComparison.Ordinal)) total += 50;
            else if (foldedTitle.Contains(term, StringComparison.Ordinal)) total += 40;
            else if (address.Contains(term, StringComparison.Ordinal)) total += 20;
            else return null;
        }
        return total;
    }

    /// <summary>The items that match, best first; equal scores keep their original order.</summary>
    public static List<T> Rank<T>(IEnumerable<T> items, string query, Func<T, string> title, Func<T, string> url) =>
        items.Select((item, offset) => (item, offset, score: Score(query, title(item), url(item))))
            .Where(x => x.score is not null)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.offset)
            .Select(x => x.item)
            .ToList();

    /// <summary>Case-, accent- and width-insensitive form.</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) current.Append(c);
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    private static string StrippingScheme(string url)
    {
        var index = url.IndexOf("://", StringComparison.Ordinal);
        return index < 0 ? url : url[(index + 3)..];
    }
}
