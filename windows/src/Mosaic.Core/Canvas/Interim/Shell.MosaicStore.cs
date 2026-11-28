// INTERIM: a verbatim copy of the shell area's MosaicStore.cs (win2/shell f740658), namespace changed,
// so the canvas builds against the real model before it lands in win-next. Delete this folder
// then and change `using Mosaic.Core.Canvas.Interim;` to `using Mosaic.Core.Shell;`.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Canvas.Interim;

/// <summary>
/// The active Mosaic and the user's edited copy of every preset. Port of
/// <c>Sources/Mosaic/Shell/MosaicStore.swift</c>; same file (<c>Shell\mosaics.json</c> under the
/// data folder, <c>{"active": …, "layouts": {…}}</c>, written as macOS writes it).
/// </summary>
/// <remarks>
/// The window is drawn from <see cref="EffectiveLayout"/>. Edits go through <see cref="Update"/>
/// or <see cref="ReplaceActiveLayout"/>; both normalise and save. Switching presets only swaps
/// the value the views read, so no page is reloaded and no tab is touched.
/// A macOS file's Safari entry is kept untouched in the file (so it survives a round trip) but
/// never used; if Safari was active, Edge is.
/// Single-threaded (UI thread); saving happens on a background task, latest state wins.
/// Owner: shell.
/// </remarks>
public sealed class MosaicStore
{
    private readonly string? filePath;
    private readonly Dictionary<MosaicPresetID, MosaicLayout> layouts = [];
    /// <summary>Entries of the file this version does not use (macOS's "safari"), written back as they were.</summary>
    private readonly Dictionary<string, JsonNode> foreignLayouts = new(StringComparer.Ordinal);
    private readonly object saveLock = new();
    private Task saving = Task.CompletedTask;
    private string? pendingText;

    /// <summary>Raised after anything that changes what the window shows or what is saved.</summary>
    public event Action? Changed;

    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "Shell", "mosaics.json");

    /// <summary>Reads <paramref name="filePath"/> (missing or damaged: factory presets); null keeps everything in memory.</summary>
    public MosaicStore(string? filePath)
    {
        this.filePath = filePath;
        foreach (var preset in Enum.GetValues<MosaicPresetID>()) layouts[preset] = MosaicLayout.Factory(preset);
        ActivePreset = MosaicPresetID.ZenArc;
        if (filePath is null) return;
        try
        {
            if (File.Exists(filePath)) Load(File.ReadAllText(filePath, Encoding.UTF8));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Preferences are never a reason to fail: defaults.
        }
    }

    private void Load(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject state) return;
        var active = SwiftDecode.TryString(state["active"]);
        // Swift decodes the whole state or nothing: an unknown active preset means defaults.
        // Safari is the exception, so that a macOS profile still opens with its own look.
        if (active == "safari") ActivePreset = MosaicPresetID.Edge;
        else if (JsonEnum.Parse<MosaicPresetID>(active) is { } preset) ActivePreset = preset;
        if (state["layouts"] is not JsonObject saved) return;
        foreach (var (key, node) in saved)
        {
            if (JsonEnum.Parse<MosaicPresetID>(key) is { } id && MosaicLayout.Decode(node) is { } layout)
            {
                layouts[id] = layout.Normalized();
            }
            else if (node is not null)
            {
                foreignLayouts[key] = node.DeepClone();
            }
        }
        // Safari was active on the Mac and Edge has never been customised here: Edge with the
        // Safari layout's look.
        if (active == "safari" && foreignLayouts.TryGetValue("safari", out var safari)
            && layouts[MosaicPresetID.Edge].ContentEquals(MosaicLayout.Factory(MosaicPresetID.Edge))
            && MosaicLayout.Decode(safari) is { } converted)
        {
            layouts[MosaicPresetID.Edge] = converted.Normalized();
        }
    }

    /// <summary>The preset whose (edited) layout is in use.</summary>
    public MosaicPresetID ActivePreset { get; private set; }

    /// <summary>Shown instead of the active layout while previewing from Settings › Mosaics.</summary>
    public MosaicLayout? PreviewLayout { get; private set; }

    /// <summary>The active preset's layout as the user has configured it.</summary>
    public MosaicLayout Layout => layouts[ActivePreset];

    /// <summary>What the window shows right now (the preview while previewing).</summary>
    public MosaicLayout EffectiveLayout => PreviewLayout ?? Layout;

    public MosaicLayout LayoutFor(MosaicPresetID preset) => layouts[preset];

    public MosaicPresetID? PreviewedPreset => PreviewLayout?.Preset;

    /// <summary>Whether <paramref name="preset"/> differs from its factory layout.</summary>
    public bool IsCustomized(MosaicPresetID preset) => !layouts[preset].ContentEquals(MosaicLayout.Factory(preset));

    // MARK: Changing

    /// <summary>Makes <paramref name="preset"/> the active Mosaic (with the user's edits to it).</summary>
    public void Apply(MosaicPresetID preset)
    {
        var hadPreview = PreviewLayout is not null;
        PreviewLayout = null;
        if (preset == ActivePreset)
        {
            if (hadPreview) Changed?.Invoke();
            return;
        }
        ActivePreset = preset;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Edits the active layout in place.</summary>
    public void Update(Action<MosaicLayout> change)
    {
        var copy = Layout.Clone();
        change(copy);
        ReplaceActiveLayout(copy);
    }

    /// <summary>Replaces the active preset's layout (layout editor, import). Its preset becomes the active one's.</summary>
    public void ReplaceActiveLayout(MosaicLayout layout)
    {
        var normalized = layout.Normalized();
        if (normalized.ContentEquals(Layout)) return;
        layouts[ActivePreset] = normalized;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Imports a layout file's layout: its preset becomes active and holds the imported layout.</summary>
    public void Import(MosaicLayout layout)
    {
        PreviewLayout = null;
        var normalized = layout.Normalized();
        ActivePreset = normalized.Preset;
        layouts[ActivePreset] = normalized;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Returns <paramref name="preset"/> to its factory layout.</summary>
    public void RestoreDefaults(MosaicPresetID preset)
    {
        layouts[preset] = MosaicLayout.Factory(preset);
        if (PreviewLayout?.Preset == preset) PreviewLayout = MosaicLayout.Factory(preset);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Shows <paramref name="preset"/> in the window without making it active; <see cref="EndPreview"/> goes back.</summary>
    public void Preview(MosaicPresetID preset)
    {
        PreviewLayout = preset == ActivePreset ? null : layouts[preset];
        Changed?.Invoke();
    }

    public void EndPreview()
    {
        if (PreviewLayout is null) return;
        PreviewLayout = null;
        Changed?.Invoke();
    }

    // MARK: Persistence

    /// <summary>The file's text for the current state, exactly as macOS writes it.</summary>
    public string StateJson()
    {
        var saved = new JsonObject();
        foreach (var (key, node) in foreignLayouts) saved[key] = node.DeepClone();
        foreach (var (preset, layout) in layouts) saved[preset.Raw()] = layout.ToJsonObject();
        return SwiftJson.Write(new JsonObject { ["active"] = ActivePreset.Raw(), ["layouts"] = saved });
    }

    private void Save()
    {
        if (filePath is null) return;
        var text = StateJson();
        lock (saveLock)
        {
            var first = pendingText is null;
            pendingText = text;
            if (first) saving = saving.ContinueWith(_ => WritePending(), TaskScheduler.Default);
        }
    }

    private void WritePending()
    {
        string? text;
        lock (saveLock)
        {
            text = pendingText;
            pendingText = null;
        }
        if (text is null || filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The change lasts for this run only.
        }
    }

    /// <summary>Waits for pending writes (tests, shutdown).</summary>
    public Task FlushAsync()
    {
        lock (saveLock) return saving;
    }
}
