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

/// <summary>The shell's <see cref="MosaicStore"/> as the canvas's host.</summary>
public sealed class MosaicStoreHost(MosaicStore store) : ICanvasLayoutHost
{
    public MosaicStore Store => store;
    public MosaicPresetID ActivePreset => store.ActivePreset;
    public MosaicLayout Layout => store.Layout;
    public void ReplaceActiveLayout(MosaicLayout layout) => store.ReplaceActiveLayout(layout);
    public void ApplyPreset(MosaicPresetID preset) => store.Apply(preset);
    public void RestoreDefaults(MosaicPresetID preset) => store.RestoreDefaults(preset);
    public bool IsCustomized(MosaicPresetID preset) => store.IsCustomized(preset);

    public event Action? LayoutChanged
    {
        add => store.Changed += value;
        remove => store.Changed -= value;
    }
}

/// <summary>How the canvas is wired into the app (set by the shell/tabs areas; read once, lazily).</summary>
public static class CanvasServices
{
    /// <summary>
    /// The shell's layout store. Until set, an in-memory <see cref="MosaicStore"/> is used
    /// (once the shell's model is in win-next the default becomes <c>model.Mosaics</c>).
    /// </summary>
    public static Func<BrowserModel, ICanvasLayoutHost>? HostFactory { get; set; }

    /// <summary>The selected workspace (ID as stored in <c>workspaceBackgrounds</c>, and its name).</summary>
    public static Func<BrowserModel, (string Id, string Name)>? Workspace { get; set; }

    /// <summary>Shows the editor in a mode (the app opens <c>mosaic://customize</c>).</summary>
    public static Action<BrowserModel, CanvasController.EditorMode>? ShowEditor { get; set; }

    /// <summary>Opens Settings › Mosaics (the shell; for <c>canvasMode presets</c>).</summary>
    public static Action<BrowserModel>? ShowMosaicsSettings { get; set; }

    /// <summary>Saved configurations: <c>%LOCALAPPDATA%\Mosaic\Canvas\configurations.json</c> (macOS: Application Support/Mosaic/Canvas).</summary>
    public static string LibraryPath => Path.Combine(AppPaths.DataRoot, "Canvas", "configurations.json");

    internal static CanvasController Create(BrowserModel model)
    {
        var controller = new CanvasController(HostFactory?.Invoke(model) ?? new MosaicStoreHost(new MosaicStore(null)), LibraryPath,
            () => Workspace?.Invoke(model) ?? ("default", "Workspace"));
        controller.Notice += text => model.Notice = text;
        controller.ShowRequested += mode => ShowEditor?.Invoke(model, mode);
        return controller;
    }
}
