using Mosaic.Core.Extensions;

namespace Mosaic.Core.Browser;

/// <summary>
/// One row of the Extensions dialog: an installed extension plus where it is running right now.
/// </summary>
/// <remarks>
/// Rows are kept and updated in place when the library changes, so a row the user has expanded
/// stays expanded while its extension loads or is toggled.
/// </remarks>
public sealed class ExtensionItem : ObservableObject
{
    internal ExtensionItem(InstalledExtension extension) => Extension = extension;

    public InstalledExtension Extension { get; private set; }

    public string Name => Extension.Name;
    public string Version => Extension.Version;
    public string Summary => Extension.Summary;
    public bool HasSummary => Extension.Summary.Length > 0;
    public string ManifestBadge => $"MV{Extension.ManifestVersion}";
    public bool IsEnabled => Extension.Enabled;
    public bool HasPopup => Extension.HasPopup;
    public bool HasOptions => Extension.HasOptions;
    public IReadOnlyList<CompatibilityNote> Notes => Extension.Notes;
    public bool HasNotes => Extension.Notes.Count > 0;
    public string EnableLabel => $"Enable {Extension.Name}";

    /// <summary>Loaded in the running Gecko engine.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Absolute path of the icon, or null.</summary>
    public string? IconFile { get; private set; }

    /// <summary>Also loaded, unconverted, in the Chromium profile (experimental).</summary>
    public bool IsInChromium { get; private set; }

    /// <summary>Why Chromium would not take the extension, when it was offered and refused.</summary>
    public string? ChromiumError { get; private set; }

    public bool HasChromiumError => ChromiumError is not null;

    /// <summary>The most serious compatibility note; null when the conversion had nothing to report.</summary>
    public NoteSeverity? WorstSeverity => HasNotes ? Extension.Notes.Max(note => note.Severity) : null;

    /// <summary>Label of the button that reveals the notes, worded by how bad they are.</summary>
    public string CompatibilityLabel => WorstSeverity switch
    {
        NoteSeverity.Blocker => "May not work on Gecko",
        NoteSeverity.Warning => "Partly compatible",
        _ => "Conversion notes",
    };

    internal void Update(InstalledExtension extension, bool isRunning, string? iconFile, bool isInChromium, string? chromiumError)
    {
        Extension = extension;
        IsRunning = isRunning;
        IconFile = iconFile;
        IsInChromium = isInChromium;
        ChromiumError = chromiumError;
        // Everything a row shows derives from the values above; announce the lot. This also puts
        // a toggle the user flipped back in step when the change could not be made.
        Notify(string.Empty);
    }
}
