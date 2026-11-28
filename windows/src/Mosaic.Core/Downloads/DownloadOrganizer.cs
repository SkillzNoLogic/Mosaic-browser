using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Downloads;

/// <summary>Kinds of files, for sorting downloads into subfolders. Raw values match macOS.</summary>
public enum DownloadFileCategory { Images, Documents, Audio, Video, Archives, Installers }

public static class DownloadFileCategories
{
    public static IReadOnlyList<DownloadFileCategory> All { get; } = Enum.GetValues<DownloadFileCategory>();

    /// <summary>The macOS raw value ("images", "installers"…), used in saved settings.</summary>
    public static string Id(this DownloadFileCategory category) => category.ToString().ToLowerInvariant();

    public static DownloadFileCategory? FromId(string? id) =>
        All.Cast<DownloadFileCategory?>().FirstOrDefault(category => category!.Value.Id() == id);

    public static string Title(this DownloadFileCategory category) => category switch
    {
        DownloadFileCategory.Images => "Images",
        DownloadFileCategory.Documents => "Documents",
        DownloadFileCategory.Audio => "Audio",
        DownloadFileCategory.Video => "Video",
        DownloadFileCategory.Archives => "Archives",
        _ => "Apps & Installers",
    };

    /// <summary>Default name of the subfolder.</summary>
    public static string DefaultFolder(this DownloadFileCategory category) => category switch
    {
        DownloadFileCategory.Images => "Images",
        DownloadFileCategory.Documents => "Documents",
        DownloadFileCategory.Audio => "Music",
        DownloadFileCategory.Video => "Videos",
        DownloadFileCategory.Archives => "Archives",
        _ => "Installers",
    };

    /// <summary>Icon name (<c>Icons.Make</c>), the macOS SF Symbol name.</summary>
    public static string Symbol(this DownloadFileCategory category) => category switch
    {
        DownloadFileCategory.Images => "photo",
        DownloadFileCategory.Documents => "doc.text",
        DownloadFileCategory.Audio => "music.note",
        DownloadFileCategory.Video => "film",
        DownloadFileCategory.Archives => "archivebox",
        _ => "shippingbox",
    };

    /// <summary>A few examples, for the settings page.</summary>
    public static string Examples(this DownloadFileCategory category) => category switch
    {
        DownloadFileCategory.Images => "JPEG, PNG, HEIC, GIF, SVG",
        DownloadFileCategory.Documents => "PDF, Word, Excel, PowerPoint, text",
        DownloadFileCategory.Audio => "MP3, AAC, WAV, FLAC",
        DownloadFileCategory.Video => "MP4, MOV, MKV, WebM",
        DownloadFileCategory.Archives => "ZIP, 7z, RAR, tar.gz",
        _ => "EXE, MSI, MSIX, ISO",
    };

    private static readonly Dictionary<string, DownloadFileCategory> ByExtension = Build();

    private static Dictionary<string, DownloadFileCategory> Build()
    {
        var table = new Dictionary<DownloadFileCategory, string[]>
        {
            [DownloadFileCategory.Images] = ["jpg", "jpeg", "png", "gif", "heic", "heif", "webp", "tif", "tiff", "bmp", "svg",
                "ico", "raw", "cr2", "nef", "arw", "dng", "avif", "psd", "jxl"],
            [DownloadFileCategory.Documents] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "md", "csv",
                "odt", "ods", "odp", "pages", "numbers", "key", "epub", "tex", "json", "xml", "xps", "oxps", "one"],
            [DownloadFileCategory.Audio] = ["mp3", "m4a", "aac", "wav", "aif", "aiff", "flac", "ogg", "opus", "wma", "alac",
                "mid", "midi"],
            [DownloadFileCategory.Video] = ["mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "3gp", "ts"],
            [DownloadFileCategory.Archives] = ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "lz", "z", "cab",
                "sit", "sitx"],
            [DownloadFileCategory.Installers] = ["dmg", "pkg", "mpkg", "app", "iso", "img", "exe", "msi", "msix",
                "msixbundle", "appx", "appxbundle", "apk", "ipa", "deb", "rpm", "appimage"],
        };
        var map = new Dictionary<string, DownloadFileCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in table)
        {
            foreach (var ext in extensions) map[ext] = category;
        }
        return map;
    }

    /// <summary>The category of a file, from its extension or, failing that, its MIME type.</summary>
    public static DownloadFileCategory? Of(string fileName, string? mimeType = null)
    {
        if (ByExtension.TryGetValue(DownloadFileName.PathExtension(fileName), out var category)) return category;
        var mime = mimeType?.ToLowerInvariant().Split(';')[0].Trim();
        if (string.IsNullOrEmpty(mime)) return null;
        if (mime.StartsWith("image/", StringComparison.Ordinal)) return DownloadFileCategory.Images;
        if (mime.StartsWith("audio/", StringComparison.Ordinal)) return DownloadFileCategory.Audio;
        if (mime.StartsWith("video/", StringComparison.Ordinal)) return DownloadFileCategory.Video;
        if (mime.StartsWith("text/", StringComparison.Ordinal) || mime == "application/pdf" ||
            mime.Contains("officedocument", StringComparison.Ordinal) || mime == "application/msword")
        {
            return DownloadFileCategory.Documents;
        }
        if (mime is "application/zip" or "application/x-7z-compressed" or "application/x-rar-compressed"
            or "application/gzip" or "application/x-tar" or "application/x-bzip2" or "application/x-xz")
        {
            return DownloadFileCategory.Archives;
        }
        if (mime is "application/x-apple-diskimage" or "application/vnd.apple.installer+xml"
            or "application/x-msdownload" or "application/x-msi" or "application/x-ms-installer"
            or "application/msix" or "application/vnd.ms-appx")
        {
            return DownloadFileCategory.Installers;
        }
        return null;
    }
}

/// <summary>
/// Sorting downloads into subfolders of the download folder by kind of file.
/// </summary>
/// <remarks>
/// Off by default. When on, each enabled category goes into its folder (created as needed); files
/// of other kinds, and files the user placed explicitly with "ask where to save", stay where they
/// were saved. Port of <c>DownloadOrganizationRules</c> (DownloadOrganizer.swift).
/// </remarks>
public sealed record DownloadOrganizationRules
{
    public DownloadOrganizationRules(bool isEnabled = false, IReadOnlyDictionary<DownloadFileCategory, string>? folders = null)
    {
        IsEnabled = isEnabled;
        Folders = folders ?? DownloadFileCategories.All.ToDictionary(category => category, category => category.DefaultFolder());
    }

    public bool IsEnabled { get; init; }

    /// <summary>Folder name per category; a category missing here is not sorted.</summary>
    public IReadOnlyDictionary<DownloadFileCategory, string> Folders { get; init; }

    /// <summary>A copy with <paramref name="category"/>'s folder set (null: not sorted).</summary>
    public DownloadOrganizationRules WithFolder(DownloadFileCategory category, string? folder)
    {
        var folders = Folders.ToDictionary();
        if (folder is null) folders.Remove(category);
        else folders[category] = folder;
        return this with { Folders = folders };
    }

    /// <summary>The subfolder (a single, sanitised path component) for a file, or null to leave it in the download folder.</summary>
    public string? Subfolder(string fileName, string? mimeType = null)
    {
        if (!IsEnabled || DownloadFileCategories.Of(fileName, mimeType) is not { } category ||
            !Folders.TryGetValue(category, out var folder))
        {
            return null;
        }
        var clean = DownloadFileName.Sanitize(folder);
        if (folder.Trim().Length == 0 || (clean == DownloadFileName.Fallback && folder != clean)) return null;
        return clean;
    }

    /// <summary>Where a file named <paramref name="fileName"/> belongs, given the download folder.</summary>
    public string Directory(string fileName, string? mimeType, string downloads) =>
        Subfolder(fileName, mimeType) is { } folder ? Path.Combine(downloads, folder) : downloads;

    /// <summary>{"isEnabled": …, "folders": {"images": "Images", …}}</summary>
    public JsonObject ToJson()
    {
        var folders = new JsonObject();
        foreach (var category in DownloadFileCategories.All)
        {
            if (Folders.TryGetValue(category, out var folder)) folders[category.Id()] = folder;
        }
        return new JsonObject { ["isEnabled"] = IsEnabled, ["folders"] = folders };
    }

    /// <summary>
    /// Reads <see cref="ToJson"/>'s shape, and also the one Swift's JSONEncoder writes on macOS
    /// for a dictionary with enum keys (a flat array: key, value, key, value…).
    /// </summary>
    public static DownloadOrganizationRules FromJson(JsonNode? node)
    {
        if (node is not JsonObject json) return new DownloadOrganizationRules();
        var enabled = json["isEnabled"] is JsonValue flag && flag.TryGetValue(out bool on) && on;
        var folders = new Dictionary<DownloadFileCategory, string>();
        switch (json["folders"])
        {
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    if (DownloadFileCategories.FromId(key) is { } category && value is JsonValue text &&
                        text.TryGetValue(out string? name))
                    {
                        folders[category] = name;
                    }
                }
                break;
            case JsonArray pairs:
                for (var i = 0; i + 1 < pairs.Count; i += 2)
                {
                    if (DownloadFileCategories.FromId(pairs[i]?.GetValue<string>()) is { } category &&
                        pairs[i + 1] is JsonValue text && text.TryGetValue(out string? name))
                    {
                        folders[category] = name;
                    }
                }
                break;
            default:
                return new DownloadOrganizationRules(enabled);
        }
        return new DownloadOrganizationRules(enabled, folders);
    }

    public bool Equals(DownloadOrganizationRules? other) =>
        other is not null && IsEnabled == other.IsEnabled && Folders.Count == other.Folders.Count &&
        Folders.All(pair => other.Folders.TryGetValue(pair.Key, out var folder) && folder == pair.Value);

    public override int GetHashCode() => HashCode.Combine(IsEnabled, Folders.Count);

    internal string ToJsonString() => ToJson().ToJsonString(new JsonSerializerOptions());
}
