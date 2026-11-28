using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Library;

/// <summary>A bookmark or folder read from another browser's file, before it goes into the store.</summary>
public sealed class ImportedBookmark
{
    public string Title { get; set; } = "";
    /// <summary>Null for folders.</summary>
    public string? Url { get; set; }
    public DateTimeOffset? Added { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    /// <summary>The other browser's bookmarks bar / toolbar folder: its contents go to Mosaic's bar.</summary>
    public bool IsToolbar { get; set; }
    public List<ImportedBookmark> Children { get; set; } = [];

    public bool IsFolder => Url is null;

    /// <summary>Bookmarks (not folders) in this subtree.</summary>
    public int BookmarkCount => IsFolder ? Children.Sum(child => child.BookmarkCount) : 1;
}

/// <summary>
/// The Netscape bookmark file format (<c>&lt;!DOCTYPE NETSCAPE-Bookmark-file-1&gt;</c>) that every
/// browser imports and exports.
/// </summary>
/// <remarks>Port of <c>NetscapeBookmarks</c> in <c>Sources/MosaicCore/Library/BookmarkFiles.swift</c>.</remarks>
public static class NetscapeBookmarks
{
    private sealed class Node(ImportedBookmark item)
    {
        public ImportedBookmark Item { get; } = item;
        public List<Node> Children { get; } = [];

        public ImportedBookmark Build()
        {
            Item.Children = [.. Children.Select(child => child.Build())];
            return Item;
        }
    }

    /// <summary>
    /// Reads a bookmark file. Tolerant of the many dialects browsers write (missing <c>&lt;p&gt;</c>,
    /// unclosed <c>&lt;DT&gt;</c>, upper or lower case).
    /// </summary>
    public static IReadOnlyList<ImportedBookmark> Parse(string html)
    {
        var root = new Node(new ImportedBookmark());
        var stack = new List<Node>();
        Node? lastFolder = null;
        (Node Node, string Closing)? capture = null;
        var text = new StringBuilder();

        var index = 0;
        while (index < html.Length)
        {
            var open = html.IndexOf('<', index);
            if (open < 0)
            {
                if (capture is not null) text.Append(html, index, html.Length - index);
                break;
            }
            if (capture is not null) text.Append(html, index, open - index);
            var close = html.IndexOf('>', open);
            if (close < 0) break;
            var tag = html[(open + 1)..close];
            index = close + 1;

            var nameEnd = 0;
            while (nameEnd < tag.Length && !char.IsWhiteSpace(tag[nameEnd])) nameEnd++;
            var name = tag[..nameEnd].ToLowerInvariant();
            if (capture is { } current)
            {
                if (name == current.Closing)
                {
                    current.Node.Item.Title = DecodeEntities(text.ToString()).Trim();
                    capture = null;
                }
                continue;
            }
            var parent = stack.Count > 0 ? stack[^1] : root;
            switch (name)
            {
                case "h3":
                {
                    var attributes = ParseAttributes(tag);
                    var folder = new Node(new ImportedBookmark
                    {
                        Added = attributes.TryGetValue("add_date", out var added) ? Date(added) : null,
                        IsToolbar = attributes.TryGetValue("personal_toolbar_folder", out var toolbar)
                            && toolbar.Equals("true", StringComparison.OrdinalIgnoreCase),
                    });
                    parent.Children.Add(folder);
                    lastFolder = folder;
                    capture = (folder, "/h3");
                    text.Clear();
                    break;
                }
                case "a":
                {
                    var attributes = ParseAttributes(tag);
                    if (!attributes.TryGetValue("href", out var href) || href.Length == 0) continue;
                    var tags = attributes.TryGetValue("tags", out var list)
                        ? list.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
                        : [];
                    var link = new Node(new ImportedBookmark
                    {
                        Url = href,
                        Added = attributes.TryGetValue("add_date", out var added) ? Date(added) : null,
                        Tags = tags,
                    });
                    parent.Children.Add(link);
                    lastFolder = null;
                    capture = (link, "/a");
                    text.Clear();
                    break;
                }
                case "dl":
                    // A list opens the folder whose heading came just before it; the first list is the root.
                    if (lastFolder is not null) stack.Add(lastFolder);
                    else stack.Add(stack.Count == 0 ? root : parent);
                    lastFolder = null;
                    break;
                case "/dl":
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    lastFolder = null;
                    break;
            }
        }
        return [.. root.Children.Select(child => child.Build())];
    }

    /// <summary>
    /// Writes a bookmark file: the bar as the toolbar folder, then everything in Other Bookmarks
    /// at the top level (the layout Chrome and Safari use).
    /// </summary>
    public static string Export(IEnumerable<Bookmark> bookmarks)
    {
        var all = bookmarks.ToList();
        var byParent = all.Where(b => b.ParentId is not null)
            .GroupBy(b => b.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(b => b.Position).ToList(), StringComparer.Ordinal);
        var lines = new List<string>
        {
            "<!DOCTYPE NETSCAPE-Bookmark-file-1>",
            "<!-- This is an automatically generated file.",
            "     It will be read and overwritten.",
            "     DO NOT EDIT! -->",
            "<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">",
            "<TITLE>Bookmarks</TITLE>",
            "<H1>Bookmarks</H1>",
            "<DL><p>",
        };

        void Write(IEnumerable<Bookmark> items, int depth)
        {
            var indent = new string(' ', depth * 4);
            foreach (var item in items)
            {
                var added = $"ADD_DATE=\"{item.Created.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}\"";
                if (item.IsFolder)
                {
                    var modified = $"LAST_MODIFIED=\"{item.Modified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}\"";
                    var toolbar = item.Id == Bookmark.BarId ? " PERSONAL_TOOLBAR_FOLDER=\"true\"" : "";
                    lines.Add($"{indent}<DT><H3 {added} {modified}{toolbar}>{Escape(item.Title)}</H3>");
                    lines.Add($"{indent}<DL><p>");
                    Write(byParent.GetValueOrDefault(item.Id) ?? [], depth + 1);
                    lines.Add($"{indent}</DL><p>");
                }
                else if (item.Url is { } url)
                {
                    var tags = item.Tags.Count == 0 ? "" : $" TAGS=\"{Escape(string.Join(",", item.Tags))}\"";
                    lines.Add($"{indent}<DT><A HREF=\"{Escape(url)}\" {added}{tags}>{Escape(item.Title)}</A>");
                }
            }
        }

        if (all.FirstOrDefault(b => b.Id == Bookmark.BarId) is { } bar) Write([bar], 1);
        Write(byParent.GetValueOrDefault(Bookmark.OtherId) ?? [], 1);
        lines.Add("</DL><p>");
        return string.Join("\n", lines) + "\n";
    }

    internal static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < tag.Length && !char.IsWhiteSpace(tag[i])) i++;
        while (i < tag.Length)
        {
            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
            var nameStart = i;
            while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] != '=') i++;
            var name = tag[nameStart..i].ToLowerInvariant();
            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
            if (i >= tag.Length || tag[i] != '=')
            {
                if (name.Length > 0) attributes[name] = "";
                continue;
            }
            i++;
            while (i < tag.Length && char.IsWhiteSpace(tag[i])) i++;
            string value;
            if (i < tag.Length && tag[i] is '"' or '\'')
            {
                var quote = tag[i];
                i++;
                var start = i;
                while (i < tag.Length && tag[i] != quote) i++;
                value = tag[start..Math.Min(i, tag.Length)];
                i++;
            }
            else
            {
                var start = i;
                while (i < tag.Length && !char.IsWhiteSpace(tag[i])) i++;
                value = tag[start..i];
            }
            if (name.Length > 0) attributes[name] = DecodeEntities(value);
        }
        return attributes;
    }

    private static DateTimeOffset? Date(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) return null;
        // Some exporters write microseconds or milliseconds.
        if (seconds > 1e14) seconds /= 1_000_000;
        else if (seconds > 1e11) seconds /= 1000;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    internal static string DecodeEntities(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal)) return text;
        var result = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            var semicolon = character == '&' ? text.IndexOf(';', index, Math.Min(12, text.Length - index)) : -1;
            if (semicolon < 0)
            {
                result.Append(character);
                index++;
                continue;
            }
            var entity = text[(index + 1)..semicolon];
            string? decoded = entity switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" => "'",
                "nbsp" => " ",
                _ when entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(entity.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) => Scalar(hex),
                _ when entity.StartsWith('#')
                    && uint.TryParse(entity.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) => Scalar(number),
                _ => null,
            };
            if (decoded is not null)
            {
                result.Append(decoded);
                index = semicolon + 1;
            }
            else
            {
                result.Append(character);
                index++;
            }
        }
        return result.ToString();
    }

    private static string? Scalar(uint code) =>
        code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) ? char.ConvertFromUtf32((int)code) : null;
}

/// <summary>
/// Chrome (and Chromium-family browsers: Edge, Brave) keep bookmarks in a JSON file named
/// <c>Bookmarks</c> in the profile folder.
/// </summary>
/// <remarks>
/// Port of <c>ChromeBookmarks</c> in <c>Sources/MosaicCore/Library/BookmarkFiles.swift</c>,
/// plus <see cref="Export"/> (the same layout Chrome writes) and Windows profile locations.
/// </remarks>
public static class ChromeBookmarks
{
    /// <summary>Seconds between 1601-01-01 (Chrome's epoch) and 1970-01-01.</summary>
    private const long EpochOffsetSeconds = 11_644_473_600;

    /// <summary>Bookmark files of the Chromium-family browsers installed for this user (Windows locations), by name.</summary>
    public static IReadOnlyList<(string Browser, string Path)> KnownFiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        (string, string)[] candidates =
        [
            ("Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Bookmarks")),
            ("Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Bookmarks")),
            ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Bookmarks")),
        ];
        return [.. candidates.Where(candidate => File.Exists(candidate.Item2))];
    }

    /// <summary>The bookmarks in a Chrome <c>Bookmarks</c> file, or null when it is not one.</summary>
    public static IReadOnlyList<ImportedBookmark>? Parse(string json)
    {
        JsonObject? roots;
        try
        {
            roots = (JsonNode.Parse(json) as JsonObject)?["roots"] as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (roots is null) return null;
        var result = new List<ImportedBookmark>();
        if (roots["bookmark_bar"] is JsonObject bar)
        {
            var folder = Node(bar) ?? new ImportedBookmark { Title = "Bookmarks Bar" };
            folder.IsToolbar = true;
            result.Add(folder);
        }
        if (roots["other"] is JsonObject other && Node(other) is { } otherFolder) result.AddRange(otherFolder.Children);
        if (roots["synced"] is JsonObject synced && Node(synced) is { Children.Count: > 0 } syncedFolder)
        {
            if (syncedFolder.Title.Length == 0) syncedFolder.Title = "Mobile Bookmarks";
            result.Add(syncedFolder);
        }
        return result;
    }

    private static ImportedBookmark? Node(JsonObject json)
    {
        var title = Text(json["name"]) ?? "";
        DateTimeOffset? added = null;
        if (double.TryParse(Text(json["date_added"]), NumberStyles.Float, CultureInfo.InvariantCulture, out var micros) && micros > 0)
        {
            added = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(micros / 1000 - EpochOffsetSeconds * 1000.0));
        }
        switch (Text(json["type"]))
        {
            case "url":
                return Text(json["url"]) is { } url ? new ImportedBookmark { Title = title, Url = url, Added = added } : null;
            case "folder":
                var children = (json["children"] as JsonArray ?? []).OfType<JsonObject>().Select(Node).OfType<ImportedBookmark>();
                return new ImportedBookmark { Title = title, Added = added, Children = [.. children] };
            default:
                return null;
        }
    }

    /// <summary>
    /// Writes Mosaic's bookmarks as a Chrome <c>Bookmarks</c> file (bar → <c>bookmark_bar</c>,
    /// Other Bookmarks → <c>other</c>). Tags have no place in Chrome's format and are left out.
    /// </summary>
    public static string Export(IEnumerable<Bookmark> bookmarks)
    {
        var all = bookmarks.ToList();
        var byParent = all.Where(b => b.ParentId is not null)
            .GroupBy(b => b.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(b => b.Position).ToList(), StringComparer.Ordinal);
        var nextId = 1;

        JsonObject Write(Bookmark item, string? rootName = null)
        {
            var node = new JsonObject
            {
                ["date_added"] = ChromeTime(item.Created),
                ["guid"] = Guid.NewGuid().ToString(),
                ["id"] = (nextId++).ToString(CultureInfo.InvariantCulture),
                ["name"] = rootName ?? item.Title,
                ["type"] = item.IsFolder ? "folder" : "url",
            };
            if (item.IsFolder)
            {
                node["date_modified"] = ChromeTime(item.Modified);
                node["children"] = new JsonArray([.. (byParent.GetValueOrDefault(item.Id) ?? []).Select(child => (JsonNode)Write(child))]);
            }
            else
            {
                node["url"] = item.Url ?? "";
            }
            return node;
        }

        JsonObject Root(string id, string name)
        {
            var folder = all.FirstOrDefault(b => b.Id == id)
                ?? new Bookmark { Id = id, Kind = BookmarkKind.Folder, Title = name, Created = DateTimeOffset.UnixEpoch, Modified = DateTimeOffset.UnixEpoch };
            return Write(folder, name);
        }

        var document = new JsonObject
        {
            ["roots"] = new JsonObject
            {
                ["bookmark_bar"] = Root(Bookmark.BarId, "Bookmarks bar"),
                ["other"] = Root(Bookmark.OtherId, "Other bookmarks"),
                ["synced"] = Write(new Bookmark
                {
                    Id = "synced", Kind = BookmarkKind.Folder, Created = DateTimeOffset.UnixEpoch, Modified = DateTimeOffset.UnixEpoch,
                }, "Mobile bookmarks"),
            },
            ["version"] = 1,
        };
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ChromeTime(DateTimeOffset time) =>
        ((time.ToUnixTimeMilliseconds() + EpochOffsetSeconds * 1000) * 1000).ToString(CultureInfo.InvariantCulture);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
