using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mosaic.Core.Downloads;

/// <summary>
/// File names for downloads: turning whatever a server or page suggested into a safe name for
/// the Windows file system, and finding a free name in the download folder.
/// </summary>
/// <remarks>Port of <c>Sources/MosaicCore/Downloads/DownloadFileName.swift</c>, with Windows' rules.</remarks>
public static partial class DownloadFileName
{
    /// <summary>Used when nothing usable is left of the suggested name.</summary>
    public const string Fallback = "download";

    /// <summary>
    /// Longest name, in UTF-16 code units. NTFS allows 255; the margin leaves room for Gecko's
    /// <c>.part</c> file and a " (12)" inserted by <see cref="Unique"/>.
    /// </summary>
    public const int MaxLength = 240;

    /// <summary>
    /// Extensions made of two parts that must stay together when a number is inserted
    /// ("archive (1).tar.gz", not "archive.tar (1).gz").
    /// </summary>
    internal static readonly string[] CompoundExtensions = ["tar.gz", "tar.bz2", "tar.xz", "tar.zst", "tar.lz", "tar.z"];

    /// <summary>
    /// Characters that reorder text on screen (right-to-left override and friends). A name such as
    /// "invoice&lt;U+202E&gt;fdp.exe" displays as "invoiceexe.pdf"; they are never legitimate in a
    /// file name, so they are removed (and flagged by <see cref="DownloadSafety"/>).
    /// </summary>
    public static readonly IReadOnlySet<int> BidiControls = new HashSet<int>
    {
        0x200E, 0x200F, 0x061C,
        0x202A, 0x202B, 0x202C, 0x202D, 0x202E,
        0x2066, 0x2067, 0x2068, 0x2069,
    };

    /// <summary>Names Windows reserves for devices, with or without an extension ("CON.txt" too).</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Makes <paramref name="suggested"/> safe to use as a single file name on Windows:
    /// <list type="bullet">
    /// <item>only the last path component is kept (no "..\", no folders);</item>
    /// <item>control characters and bidirectional controls are removed; ":" becomes "-" and the other
    /// characters Windows forbids (&lt; &gt; " | ? *) become "_";</item>
    /// <item>leading dots (hidden files) and surrounding spaces/dots are trimmed (Windows drops
    /// trailing dots and spaces itself, which would change the extension);</item>
    /// <item>device names (CON, NUL, COM1…) get a "_" prefix;</item>
    /// <item>the name is limited to <see cref="MaxLength"/> characters, keeping its extension.</item>
    /// </list>
    /// </summary>
    public static string Sanitize(string? suggested)
    {
        var name = suggested ?? "";
        // Keep only the last component, whichever separator the source used.
        var parts = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        name = parts.Length > 0 ? parts[^1] : "";

        var builder = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (BidiControls.Contains(rune.Value)) continue;
            // The zero-width joiner holds emoji sequences together; keep it.
            if (rune.Value == 0x200D)
            {
                builder.Append(rune.ToString());
                continue;
            }
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.Surrogate:
                    continue;
            }
            switch (rune.Value)
            {
                case ':':
                    builder.Append('-');
                    break;
                case '<' or '>' or '"' or '|' or '?' or '*':
                    builder.Append('_');
                    break;
                default:
                    builder.Append(rune.ToString());
                    break;
            }
        }
        name = builder.ToString();
        // Collapse runs of whitespace (used to push a real extension out of sight).
        name = Whitespace().Replace(name, " ");
        name = name.Trim(' ', '.', '\t');
        // Hidden files are not what anyone means to download.
        name = name.TrimStart('.').Trim(' ');
        if (name.Length == 0 || name is "." or "..") return Fallback;
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem)) name = "_" + name;
        return LimitLength(name, MaxLength);
    }

    /// <summary>
    /// The name split into stem and extension (the extension keeps its case). Compound archive
    /// extensions are kept whole. A name with no extension has an empty one.
    /// </summary>
    public static (string Stem, string Ext) Split(string name)
    {
        foreach (var compound in CompoundExtensions)
        {
            if (name.Length > compound.Length + 1 && name.EndsWith("." + compound, StringComparison.OrdinalIgnoreCase))
            {
                return (name[..^(compound.Length + 1)], name[^compound.Length..]);
            }
        }
        var dot = name.LastIndexOf('.');
        if (dot <= 0) return (name, "");
        var ext = name[(dot + 1)..];
        // "version 1.2" or "notes." are not extensions.
        if (ext.Length == 0 || ext.Length > 12 || ext.Contains(' ')) return (name, "");
        return (name[..dot], ext);
    }

    /// <summary>The lowercased extension of <paramref name="name"/> (the last one: "gz" for "a.tar.gz").</summary>
    public static string PathExtension(string name)
    {
        var ext = Split(name).Ext.ToLowerInvariant();
        var dot = ext.LastIndexOf('.');
        return dot >= 0 ? ext[(dot + 1)..] : ext;
    }

    /// <summary>
    /// <paramref name="name"/>, or "name (1).ext", "name (2).ext"… — the first one
    /// <paramref name="exists"/> says is free. Callers compare case-insensitively, as Windows does.
    /// </summary>
    public static string Unique(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var (stem, ext) = Split(name);
        var suffix = ext.Length == 0 ? "" : "." + ext;
        for (var number = 1; number < 10_000; number++)
        {
            var candidate = LimitLength($"{stem} ({number}){suffix}", MaxLength);
            if (!exists(candidate)) return candidate;
        }
        return LimitLength($"{stem} {Guid.NewGuid().ToString("N")[..8]}{suffix}", MaxLength);
    }

    /// <summary>
    /// A name for a URL that came without one (no Content-Disposition): its last path component,
    /// percent-decoded, or the host.
    /// </summary>
    public static string Suggested(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Fallback;
        var path = uri.IsFile || uri.Scheme is "http" or "https" or "ftp" ? uri.AbsolutePath : "";
        var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!string.IsNullOrEmpty(last))
        {
            string decoded;
            try { decoded = Uri.UnescapeDataString(last); }
            catch (UriFormatException) { decoded = last; }
            return Sanitize(decoded);
        }
        return Sanitize(string.IsNullOrEmpty(uri.Host) ? Fallback : uri.Host);
    }

    /// <summary>
    /// The file name a <c>Content-Disposition</c> header gives, before sanitising: <c>filename*</c>
    /// (RFC 5987, percent-encoded with a charset) wins over <c>filename</c>. Null when it names none.
    /// </summary>
    public static string? FromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        string? plain = null, extended = null;
        foreach (var (key, value) in DispositionParameters(header))
        {
            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase)) extended ??= DecodeExtended(value);
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase)) plain ??= value;
        }
        var result = extended ?? plain;
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private static IEnumerable<(string Key, string Value)> DispositionParameters(string header)
    {
        var index = header.IndexOf(';');
        while (index >= 0 && index < header.Length)
        {
            index++;
            while (index < header.Length && header[index] is ' ' or '\t') index++;
            var equals = header.IndexOf('=', index);
            if (equals < 0) yield break;
            var key = header[index..equals].Trim();
            index = equals + 1;
            while (index < header.Length && header[index] is ' ' or '\t') index++;
            string value;
            if (index < header.Length && header[index] == '"')
            {
                var builder = new StringBuilder();
                index++;
                while (index < header.Length && header[index] != '"')
                {
                    if (header[index] == '\\' && index + 1 < header.Length) index++;
                    builder.Append(header[index]);
                    index++;
                }
                value = builder.ToString();
                index = header.IndexOf(';', Math.Min(index, header.Length));
            }
            else
            {
                var end = header.IndexOf(';', index);
                value = (end < 0 ? header[index..] : header[index..end]).Trim();
                index = end;
            }
            yield return (key, value);
            if (index < 0) yield break;
        }
    }

    private static string? DecodeExtended(string value)
    {
        // charset'language'percent-encoded
        var first = value.IndexOf('\'');
        var second = first < 0 ? -1 : value.IndexOf('\'', first + 1);
        if (second < 0) return null;
        var charset = value[..first];
        var encoded = value[(second + 1)..];
        try
        {
            var encoding = charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase)
                ? Encoding.Latin1
                : Encoding.UTF8;
            var bytes = new List<byte>();
            for (var i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] == '%' && i + 2 < encoded.Length &&
                    byte.TryParse(encoded.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    bytes.Add(b);
                    i += 2;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(encoded[i].ToString()));
                }
            }
            return encoding.GetString([.. bytes]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static string LimitLength(string name, int maxLength)
    {
        if (name.Length <= maxLength) return name;
        var (stem, ext) = Split(name);
        var suffix = ext.Length == 0 ? "" : "." + ext;
        var room = maxLength - suffix.Length;
        if (room <= 0) return Truncate(name, 64);
        return Truncate(stem, room).TrimEnd(' ', '.') + suffix;
    }

    /// <summary>The first <paramref name="length"/> code units, never splitting a surrogate pair.</summary>
    private static string Truncate(string text, int length)
    {
        if (text.Length <= length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
}
