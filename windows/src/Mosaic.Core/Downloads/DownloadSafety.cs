using System.Text.RegularExpressions;

namespace Mosaic.Core.Downloads;

/// <summary>Kinds of files that can run code or change the system when opened.</summary>
public enum DownloadRisk
{
    /// <summary>A Windows program (.exe, .scr, .com…).</summary>
    Application,
    /// <summary>A Windows installer package (.msi, .msix…).</summary>
    Installer,
    /// <summary>A disk image Windows mounts with a double-click (.iso, .img, .vhd), which can hold programs.</summary>
    DiskImage,
    /// <summary>A script Windows runs when it is opened (.bat, .ps1, .vbs, .js, .hta…).</summary>
    Script,
    /// <summary>A shortcut that can point at a program or a command (.lnk, .url, .scf…).</summary>
    Shortcut,
    /// <summary>A file that changes system or app settings (.reg, .inf…).</summary>
    Configuration,
    /// <summary>A program for macOS, which cannot run on Windows: noted, not warned about.</summary>
    MacProgram,
}

/// <summary>Why a file's name could make it look like something it is not.</summary>
public abstract record DownloadMisleading
{
    private DownloadMisleading() { }

    /// <summary>Right-to-left override (or a similar control character) reorders the name on screen.</summary>
    public sealed record BidiOverride : DownloadMisleading
    {
        public override string Description =>
            "Its name contains invisible characters that reverse part of the text, so the name you see is not its real name.";
    }

    /// <summary>"invoice.pdf.exe": looks like a document (Windows hides the real extension by default).</summary>
    public sealed record DoubleExtension(string Shown, string Real) : DownloadMisleading
    {
        public override string Description =>
            $"Its name ends in “.{Shown}.{Real}”: it looks like a .{Shown} file but is really a .{Real} file.";
    }

    /// <summary>Runs of spaces push the real extension out of view ("photo.jpg        .exe").</summary>
    public sealed record PaddedExtension : DownloadMisleading
    {
        public override string Description => "Its name hides its real extension behind a run of spaces.";
    }

    public abstract string Description { get; }
}

/// <summary>Everything Mosaic knows about a file name's risk.</summary>
public sealed record DownloadAssessment(DownloadRisk? Risk, IReadOnlyList<DownloadMisleading> Misleading)
{
    public static readonly DownloadAssessment Safe = new(null, []);

    /// <summary>True when the user should confirm before keeping or opening the file.</summary>
    public bool NeedsWarning => Misleading.Count > 0 || (Risk is { } risk && risk != DownloadRisk.MacProgram);

    /// <summary>One or two sentences for a warning; null when there is nothing to say.</summary>
    public string? Summary
    {
        get
        {
            var parts = new List<string>();
            if (Risk is { } risk && (risk != DownloadRisk.MacProgram || Misleading.Count > 0))
            {
                parts.Add($"This file is {DownloadSafety.Describe(risk)}.");
            }
            parts.AddRange(Misleading.Select(reason => reason.Description));
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    public bool Equals(DownloadAssessment? other) =>
        other is not null && Risk == other.Risk && Misleading.SequenceEqual(other.Misleading);

    public override int GetHashCode() => HashCode.Combine(Risk, Misleading.Count);
}

/// <summary>
/// Decides which downloads deserve a warning before they are kept or opened.
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Downloads/DownloadSafety.swift</c> with Windows' dangerous types.
/// Mosaic never sends files or their details anywhere to decide this: every check here is local
/// and based on the file's name. Windows' own SmartScreen and Attachment Manager still apply to
/// whatever is opened, because every download carries the Mark of the Web
/// (see <see cref="MarkOfTheWeb"/>).
/// </remarks>
public static partial class DownloadSafety
{
    /// <summary>Shown in warnings: "This file is &lt;description&gt;."</summary>
    public static string Describe(DownloadRisk risk) => risk switch
    {
        DownloadRisk.Application => "a program",
        DownloadRisk.Installer => "an installer package",
        DownloadRisk.DiskImage => "a disk image, which can contain programs",
        DownloadRisk.Script => "a script that can run commands on your PC",
        DownloadRisk.Shortcut => "a shortcut that can start a program or a command",
        DownloadRisk.Configuration => "a file that can change system or app settings",
        _ => "a macOS program",
    };

    internal static readonly IReadOnlyDictionary<string, DownloadRisk> RiskByExtension = BuildRisks();

    private static Dictionary<string, DownloadRisk> BuildRisks()
    {
        var map = new Dictionary<string, DownloadRisk>(StringComparer.OrdinalIgnoreCase);
        void Add(DownloadRisk risk, params string[] extensions)
        {
            foreach (var ext in extensions) map[ext] = risk;
        }
        // Windows' own list of high-risk types (Attachment Manager, SmartScreen) plus Chromium's.
        Add(DownloadRisk.MacProgram,
            "app", "prefpane", "saver", "qlgenerator", "kext", "plugin", "appex", "osax", "workflow", "action",
            "widget", "systemextension", "xpc", "pkg", "mpkg", "dmg", "sparseimage", "sparsebundle", "toast", "cdr",
            "smi", "dart", "udif", "command", "tool", "terminal", "inetloc", "fileloc", "scpt", "scptd", "applescript",
            "mobileconfig", "configprofile", "mobileprovision", "provisionprofile");
        Add(DownloadRisk.Application,
            "exe", "com", "scr", "pif", "cpl", "msc", "gadget", "appref-ms", "application", "xbap", "xll", "dll",
            "ocx", "sys", "drv", "pyz", "pyzw");
        Add(DownloadRisk.Installer,
            "msi", "msp", "mst", "msix", "msixbundle", "appx", "appxbundle", "appinstaller", "vsix");
        Add(DownloadRisk.DiskImage, "iso", "img", "vhd", "vhdx");
        Add(DownloadRisk.Script,
            "bat", "cmd", "ps1", "psm1", "psd1", "ps1xml", "ps2", "ps2xml", "psc1", "psc2", "vb", "vbs", "vbe",
            "js", "jse", "ws", "wsc", "wsf", "wsh", "hta", "jar", "jnlp", "py", "pyc", "pyo", "pyw", "pl", "rb",
            "sh", "bash", "msh", "msh1", "msh2", "mshxml", "msh1xml", "msh2xml", "mcf", "shb", "shs", "chm",
            "website", "xnk");
        Add(DownloadRisk.Shortcut, "lnk", "url", "scf", "searchconnector-ms", "library-ms", "search-ms",
            "settingcontent-ms", "desktop", "shortcut");
        Add(DownloadRisk.Configuration, "reg", "inf", "ins", "isp", "theme", "themepack", "deskthemepack", "diagcab",
            "diagcfg", "diagpkg", "msrcincident", "rdp", "grp", "cer", "crt", "der", "p7b", "pfx");
        return map;
    }

    /// <summary>Extensions people trust at a glance; used to spot "invoice.pdf.exe".</summary>
    internal static readonly IReadOnlySet<string> DocumentLikeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "csv", "odt", "ods", "odp",
        "pages", "numbers", "key", "jpg", "jpeg", "png", "gif", "heic", "webp", "bmp", "tif", "tiff",
        "mp3", "m4a", "wav", "mp4", "mov", "m4v", "avi", "mkv", "html", "htm", "zip", "epub",
    };

    /// <summary>
    /// Files Mosaic may open by itself after downloading when the user asked for that: documents,
    /// pictures, audio and video. Never archives (they can unpack programs), never anything
    /// executable, never macro-enabled Office files.
    /// </summary>
    internal static readonly IReadOnlySet<string> SafeToOpenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "txt", "rtf", "md", "csv",
        "jpg", "jpeg", "png", "gif", "heic", "heif", "webp", "tif", "tiff", "bmp",
        "mp3", "m4a", "aac", "wav", "aif", "aiff", "flac", "ogg", "opus",
        "mp4", "m4v", "mov", "webm",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "epub",
    };

    [GeneratedRegex(@"\s{3,}\.[A-Za-z0-9]{1,12}$")]
    private static partial Regex PaddedAtEnd();

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,12}\s{3,}\S")]
    private static partial Regex PaddedInside();

    /// <summary>The risk of a file, judged by its (last) extension.</summary>
    public static DownloadRisk? Risk(string fileName) =>
        RiskByExtension.TryGetValue(DownloadFileName.PathExtension(fileName), out var risk) ? risk : null;

    /// <summary>Ways <paramref name="name"/> (as the server or page suggested it, before sanitising) could mislead.</summary>
    public static IReadOnlyList<DownloadMisleading> MisleadingReasons(string name)
    {
        var reasons = new List<DownloadMisleading>();
        var cleaned = new System.Text.StringBuilder(name.Length);
        var hasBidi = false;
        foreach (var rune in name.EnumerateRunes())
        {
            if (DownloadFileName.BidiControls.Contains(rune.Value)) hasBidi = true;
            else cleaned.Append(rune.ToString());
        }
        if (hasBidi) reasons.Add(new DownloadMisleading.BidiOverride());
        var text = cleaned.ToString();
        if (PaddedAtEnd().IsMatch(text) || PaddedInside().IsMatch(text)) reasons.Add(new DownloadMisleading.PaddedExtension());
        var parts = text.Trim().Split('.').Select(part => part.Trim().ToLowerInvariant()).ToArray();
        if (parts.Length >= 3)
        {
            var real = parts[^1];
            var shown = parts[^2];
            if (RiskByExtension.ContainsKey(real) && DocumentLikeExtensions.Contains(shown))
            {
                reasons.Add(new DownloadMisleading.DoubleExtension(shown, real));
            }
        }
        return reasons;
    }

    /// <summary>
    /// Full assessment. <paramref name="suggestedName"/> is the raw name (it may contain the
    /// characters that sanitising removes); <paramref name="savedName"/> is the name on disk.
    /// </summary>
    public static DownloadAssessment Assess(string suggestedName, string savedName)
    {
        var misleading = MisleadingReasons(suggestedName).ToList();
        foreach (var reason in MisleadingReasons(savedName))
        {
            if (!misleading.Contains(reason)) misleading.Add(reason);
        }
        return new DownloadAssessment(Risk(savedName), misleading);
    }

    /// <summary>Whether "open safe files after downloading" may open this file.</summary>
    public static bool IsSafeToOpenAutomatically(string name) =>
        MisleadingReasons(name).Count == 0 && Risk(name) is null &&
        SafeToOpenExtensions.Contains(DownloadFileName.PathExtension(name));
}
