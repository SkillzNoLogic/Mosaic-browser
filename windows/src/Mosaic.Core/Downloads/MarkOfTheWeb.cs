using System.Text;

namespace Mosaic.Core.Downloads;

/// <summary>
/// The Mark of the Web: the <c>Zone.Identifier</c> alternate data stream that marks a file as
/// downloaded from the internet, so SmartScreen, Office's Protected View and Windows' Attachment
/// Manager check it (and warn) when it is opened. The Windows counterpart of macOS's quarantine
/// attribute (<c>Quarantine</c> in DownloadSupport.swift).
/// </summary>
/// <remarks>
/// Chromium (WebView2) and Firefox both write the mark themselves; Mosaic makes sure it is there
/// on every completed file, whichever engine fetched it, and writes it in the same form browsers
/// use:
/// <code>
/// [ZoneTransfer]
/// ZoneId=3
/// ReferrerUrl=https://example.com/page.html
/// HostUrl=https://example.com/file.zip
/// </code>
/// Alternate data streams exist only on NTFS (and ReFS); on FAT/exFAT drives and network shares
/// that do not support them the mark cannot be stored, which <see cref="Ensure"/> reports.
/// </remarks>
public static class MarkOfTheWeb
{
    /// <summary>Name of the alternate data stream.</summary>
    public const string StreamName = "Zone.Identifier";

    /// <summary>Longest referrer recorded, as in Chromium (INTERNET_MAX_URL_LENGTH).</summary>
    private const int MaxUrlLength = 2083;

    /// <summary>URLZONE_INTERNET.</summary>
    public const int InternetZone = 3;

    /// <summary>What <see cref="Ensure"/> did.</summary>
    public enum Result
    {
        /// <summary>The file already carried a mark for the Internet (or Restricted) zone.</summary>
        AlreadyMarked,
        /// <summary>Mosaic wrote the mark.</summary>
        Written,
        /// <summary>The volume cannot store alternate data streams, or the write failed.</summary>
        Failed,
        /// <summary>Not Windows: there is nothing to write.</summary>
        Unsupported,
    }

    /// <summary>The stream's path: <c>C:\…\file.zip:Zone.Identifier</c>.</summary>
    public static string StreamPath(string filePath) => filePath + ":" + StreamName;

    /// <summary>
    /// The stream's text for a file downloaded from <paramref name="source"/>, started from the
    /// page <paramref name="referrer"/>. Only http(s)/ftp addresses are recorded, without any
    /// user name or password in them; a download that came from elsewhere (blob:, data:) is
    /// recorded as <c>about:internet</c>, as Chromium does.
    /// </summary>
    public static string Content(string? source, string? referrer)
    {
        var builder = new StringBuilder();
        builder.Append("[ZoneTransfer]\r\n");
        builder.Append("ZoneId=").Append(InternetZone).Append("\r\n");
        if (Recordable(referrer, allowFtp: false) is { Length: <= MaxUrlLength } page) builder.Append("ReferrerUrl=").Append(page).Append("\r\n");
        builder.Append("HostUrl=").Append(Recordable(source, allowFtp: true) ?? "about:internet").Append("\r\n");
        return builder.ToString();
    }

    /// <summary>The zone recorded in a stream's text, or null when it has none.</summary>
    public static int? ZoneOf(string? content)
    {
        if (content is null) return null;
        var inSection = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            var equals = line.IndexOf('=');
            if (equals < 0 || !line[..equals].Trim().Equals("ZoneId", StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(line[(equals + 1)..].Trim(), out var zone) ? zone : null;
        }
        return null;
    }

    /// <summary>The stream's text, or null when the file has none (or this is not Windows).</summary>
    public static string? Read(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return File.ReadAllText(StreamPath(filePath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Marks <paramref name="filePath"/> as downloaded from the internet unless it already carries
    /// a mark for the Internet zone or a stricter one (an engine's own mark is kept as it is).
    /// </summary>
    public static Result Ensure(string filePath, string? source, string? referrer)
    {
        if (!OperatingSystem.IsWindows()) return Result.Unsupported;
        if (ZoneOf(Read(filePath)) is >= InternetZone) return Result.AlreadyMarked;
        try
        {
            File.WriteAllText(StreamPath(filePath), Content(source, referrer), Encoding.ASCII);
            return ZoneOf(Read(filePath)) == InternetZone ? Result.Written : Result.Failed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Result.Failed;
        }
    }

    private static string? Recordable(string? url, bool allowFtp)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") && !(allowFtp && uri.Scheme == "ftp")) return null;
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        // Non-ASCII text stays percent-encoded: the stream is read as ANSI text.
        return builder.Uri.AbsoluteUri;
    }
}
