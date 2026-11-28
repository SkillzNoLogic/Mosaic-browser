using Mosaic.Core.Browser;
using Mosaic.Core.Canvas.Interim;

namespace Mosaic.Core.Canvas;

/// <summary>
/// The shell's layout store as the canvas needs it (macOS: <c>MosaicStore</c>). The shell area
/// implements this over its store and sets <see cref="CanvasServices.HostFactory"/>; every
/// canvas edit then goes through <see cref="ReplaceActiveLayout"/>, so the real window updates
/// live and no page reloads (the shell only swaps the value its views read).
/// </summary>
public interface ICanvasLayoutHost
{
    MosaicPresetID ActivePreset { get; }

    /// <summary>The active preset's (edited) layout.</summary>
    MosaicLayout Layout { get; }

    /// <summary>Shows and keeps <paramref name="layout"/> as the active preset's layout (normalised by the store).</summary>
    void ReplaceActiveLayout(MosaicLayout layout);

    /// <summary>Switches to a preset (with its edited layout).</summary>
    void ApplyPreset(MosaicPresetID preset);

    /// <summary>A preset back to its factory layout.</summary>
    void RestoreDefaults(MosaicPresetID preset);

    bool IsCustomized(MosaicPresetID preset);

    /// <summary>The layout changed (from the canvas or anywhere else, e.g. a preset switch).</summary>
    event Action? LayoutChanged;
}

/// <summary>
/// An in-memory stand-in for the shell's store until it lands (nothing is saved; the window is
/// not drawn from it). Keeps each preset's edits, like <c>MosaicStore</c>.
/// </summary>
public sealed class InterimLayoutHost : ICanvasLayoutHost
{
    private readonly Dictionary<MosaicPresetID, MosaicLayout> layouts =
        RawValues.All<MosaicPresetID>().ToDictionary(p => p, MosaicLayout.PresetLayout);

    public InterimLayoutHost(MosaicPresetID active = MosaicPresetID.ZenArc) => ActivePreset = active;

    public MosaicPresetID ActivePreset { get; private set; }

    public MosaicLayout Layout => layouts[ActivePreset];

    public event Action? LayoutChanged;

    public void ReplaceActiveLayout(MosaicLayout layout)
    {
        var normalized = layout.Normalized();
        if (normalized == layouts[ActivePreset]) return;
        layouts[ActivePreset] = normalized;
        LayoutChanged?.Invoke();
    }

    public void ApplyPreset(MosaicPresetID preset)
    {
        if (preset == ActivePreset) return;
        ActivePreset = preset;
        LayoutChanged?.Invoke();
    }

    public void RestoreDefaults(MosaicPresetID preset)
    {
        layouts[preset] = MosaicLayout.PresetLayout(preset);
        if (preset == ActivePreset) LayoutChanged?.Invoke();
    }

    public bool IsCustomized(MosaicPresetID preset) => layouts[preset] != MosaicLayout.PresetLayout(preset);
}

/// <summary>How the canvas is wired into the app (set by the shell/tabs areas; read once, lazily).</summary>
public static class CanvasServices
{
    /// <summary>The shell's layout store. Until set, an <see cref="InterimLayoutHost"/> is used.</summary>
    public static Func<BrowserModel, ICanvasLayoutHost>? HostFactory { get; set; }

    /// <summary>The selected workspace (ID as stored in <c>workspaceBackgrounds</c>, and its name).</summary>
    public static Func<BrowserModel, (string Id, string Name)>? Workspace { get; set; }

    /// <summary>Saved configurations: <c>%LOCALAPPDATA%\Mosaic\Canvas\configurations.json</c> (macOS: Application Support/Mosaic/Canvas).</summary>
    public static string LibraryPath => Path.Combine(AppPaths.DataRoot, "Canvas", "configurations.json");

    internal static CanvasController Create(BrowserModel model)
    {
        var controller = new CanvasController(HostFactory?.Invoke(model) ?? new InterimLayoutHost(), LibraryPath,
            () => Workspace?.Invoke(model) ?? ("default", "Workspace"));
        controller.Notice += text => model.Notice = text;
        return controller;
    }
}
