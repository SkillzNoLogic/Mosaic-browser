using Mosaic.Core.Search;

namespace Mosaic.Core.Library;

/// <summary>Whether a <see cref="Bookmark"/> is a page or a folder (stored as <c>url</c> / <c>folder</c>).</summary>
public enum BookmarkKind { Url, Folder }

/// <summary>
/// A bookmark or a bookmark folder. Folders form a tree under two fixed roots,
/// <see cref="BarId"/> (the bookmarks bar) and <see cref="OtherId"/> (everything else).
/// </summary>
/// <remarks>Port of <c>Bookmark</c> in <c>Sources/MosaicCore/Library/LibraryModels.swift</c>.</remarks>
public sealed record Bookmark
{
    public const string BarId = "bar";
    public const string OtherId = "other";
    public static IReadOnlySet<string> RootIds { get; } = new HashSet<string>(StringComparer.Ordinal) { BarId, OtherId };

    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public BookmarkKind Kind { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Null for folders.</summary>
    public string? Url { get; init; }
    /// <summary>Order among its siblings, from 0.</summary>
    public int Position { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }

    public bool IsFolder => Kind == BookmarkKind.Folder;
    public bool IsRoot => RootIds.Contains(Id);

    /// <summary>The title, or the address when the bookmark has none.</summary>
    public string DisplayTitle =>
        Title.Length > 0 ? Title : Url is { } url ? LibraryUrl.Display(url) : "Untitled Folder";

    internal static string KindId(BookmarkKind kind) => kind == BookmarkKind.Folder ? "folder" : "url";
    internal static BookmarkKind KindFrom(string id) => id == "folder" ? BookmarkKind.Folder : BookmarkKind.Url;
}

/// <summary>One address in history, with its aggregate counts.</summary>
public sealed record HistoryEntry(
    long Id, string Url, string Title, string Host, int VisitCount, int TypedCount,
    DateTimeOffset LastVisit, double Frecency)
{
    public string DisplayTitle => Title.Length == 0 ? LibraryUrl.Display(Url) : Title;
}

/// <summary>One visit to an address (the History page lists these, newest first).</summary>
public sealed record HistoryVisit(
    long Id, long UrlId, string Url, string Title, string Host, DateTimeOffset Time, bool Typed)
{
    public string DisplayTitle => Title.Length == 0 ? LibraryUrl.Display(Url) : Title;
}

/// <summary>How much history there is.</summary>
public readonly record struct HistoryCounts(int Addresses, int Visits);

/// <summary>A page saved to read later.</summary>
public sealed record ReadingListItem(string Id, string Url, string Title, DateTimeOffset Added, DateTimeOffset? ReadAt)
{
    public bool IsRead => ReadAt is not null;
    public string DisplayTitle => Title.Length == 0 ? LibraryUrl.Display(Url) : Title;
}

/// <summary>A search engine the user added (name, address-bar keyword, URL template with <c>%s</c>).</summary>
public sealed record CustomSearchEngine
{
    public string Id { get; init; } = "custom." + Guid.NewGuid().ToString().ToUpperInvariant();
    public required string Name { get; init; }
    /// <summary>Typed before the terms in the address bar ("w swift" searches Wikipedia).</summary>
    public string Keyword { get; init; } = "";
    /// <summary>Search URL with <c>%s</c> standing in for the terms.</summary>
    public required string Template { get; init; }
    /// <summary>Optional OpenSearch suggestion URL with <c>%s</c>.</summary>
    public string? SuggestTemplate { get; init; }

    /// <summary>The engine as a <see cref="Search.SearchEngine"/>, so it can be the default.</summary>
    public SearchEngine SearchEngine
    {
        get
        {
            var homepage = Template;
            if (Uri.TryCreate(Template.Replace("%s", "", StringComparison.Ordinal), UriKind.Absolute, out var parsed)
                && parsed.Host.Length > 0)
            {
                homepage = $"{parsed.Scheme}://{parsed.Host}/";
            }
            return new SearchEngine(Id, Name, Template, homepage);
        }
    }

    /// <summary>Why the template or keyword cannot be used, or null when it is fine.</summary>
    public static string? Problem(string name, string keyword, string template)
    {
        if (name.Trim().Length == 0) return "Give the search engine a name.";
        if (keyword.Any(char.IsWhiteSpace)) return "A keyword cannot contain spaces.";
        if (!template.Contains("%s", StringComparison.Ordinal)) return "The address must contain %s where the search terms go.";
        if (!Uri.TryCreate(template.Replace("%s", "test", StringComparison.Ordinal), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || url.Host.Length == 0)
        {
            return "The address must be a web address starting with https://.";
        }
        return null;
    }
}

/// <summary>Address helpers shared by the store and the UI.</summary>
/// <remarks>
/// Port of <c>LibraryURL</c>. Addresses are taken apart by hand rather than with
/// <see cref="Uri"/>, which would re-encode and canonicalise them; the macOS version only
/// lower-cases the scheme and host and drops the fragment.
/// </remarks>
public static class LibraryUrl
{
    /// <summary>Only real web pages (and local files) go into history.</summary>
    public static bool IsRecordable(string url) =>
        Scheme(url)?.ToLowerInvariant() is "http" or "https" or "file";

    /// <summary>The host without a leading "www.", lower-cased; empty when there is none.</summary>
    public static string Host(string url)
    {
        if (Split(url) is not { Authority: { } authority }) return "";
        var at = authority.LastIndexOf('@');
        var host = at >= 0 ? authority[(at + 1)..] : authority;
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            host = close > 0 ? host[1..close] : host[1..];
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon >= 0) host = host[..colon];
        }
        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>A short form for display: no scheme, no "www.", no trailing slash.</summary>
    public static string Display(string url)
    {
        var text = url;
        foreach (var prefix in (string[])["https://", "http://"])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        }
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        if (text.EndsWith('/') && text.Count(c => c == '/') == 1) text = text[..^1];
        return text;
    }

    /// <summary>
    /// The form used to decide whether two addresses are "the same page" for the star:
    /// scheme and host lower-cased, fragment dropped, a bare trailing slash ignored.
    /// </summary>
    public static string Normalized(string url)
    {
        var hash = url.IndexOf('#');
        var text = hash >= 0 ? url[..hash] : url;
        if (Split(text) is not { } parts) return text;
        var authority = parts.Authority;
        if (authority is not null)
        {
            var at = authority.LastIndexOf('@');
            authority = at >= 0 ? authority[..(at + 1)] + authority[(at + 1)..].ToLowerInvariant() : authority.ToLowerInvariant();
        }
        var rest = parts.Rest;
        var query = rest.IndexOf('?');
        var path = query >= 0 ? rest[..query] : rest;
        if (path == "/") rest = query >= 0 ? rest[query..] : "";
        return parts.Scheme.ToLowerInvariant() + ":" + (authority is null ? "" : "//" + authority) + rest;
    }

    /// <summary>The scheme, when <paramref name="url"/> starts with a valid one followed by ':'.</summary>
    internal static string? Scheme(string url)
    {
        var colon = url.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(url[0])) return null;
        for (var i = 1; i < colon; i++)
        {
            var c = url[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return null;
        }
        // Foundation's URL refuses text with spaces; so does history.
        if (url.Any(char.IsWhiteSpace)) return null;
        return url[..colon];
    }

    private sealed record Parts(string Scheme, string? Authority, string Rest);

    private static Parts? Split(string url)
    {
        if (Scheme(url) is not { } scheme) return null;
        var after = url[(scheme.Length + 1)..];
        if (!after.StartsWith("//", StringComparison.Ordinal)) return new Parts(scheme, null, after);
        var body = after[2..];
        var end = body.IndexOfAny(['/', '?', '#']);
        return end < 0 ? new Parts(scheme, body, "") : new Parts(scheme, body[..end], body[end..]);
    }
}
