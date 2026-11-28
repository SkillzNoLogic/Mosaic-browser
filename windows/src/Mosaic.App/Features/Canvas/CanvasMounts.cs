using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Mosaic.Core.Browser;
using Mosaic.Core.Canvas;

namespace Mosaic.App.Features.Canvas;

/// <summary>
/// Plugs the canvas into the app: the editor at <c>mosaic://customize</c> (the shell's Mosaics
/// settings page links to it), the canvas's automation commands and <c>dump</c> key, and a
/// reference to the window for file pickers. Registration only stores delegates; the canvas
/// itself is created when first used. Owner: canvas.
/// </summary>
internal static class CanvasMounts
{
    public const string Host = "customize";

    /// <summary>The main window (file pickers need its handle); set by the start-up hook.</summary>
    public static Window? Window { get; private set; }

    [ModuleInitializer]
    internal static void Register()
    {
        FeatureMounts.InternalPages[Host] = model => new CanvasPage(model);
        FeatureMounts.Startup.Add(("canvas", (_, window) => Window = window));
        CanvasAutomation.Register();
        CanvasServices.ShowEditor = Show;
    }

    /// <summary>Opens the editor in a mode (automation's <c>canvasMode</c>, the Mosaics page's buttons).</summary>
    public static void Show(BrowserModel model, CanvasController.EditorMode mode)
    {
        if (mode == CanvasController.EditorMode.Presets && CanvasServices.ShowMosaicsSettings is { } settings)
        {
            settings(model);
            return;
        }
        // Already showing the editor: the page follows the controller's mode.
        if (model.SelectedTab?.Url.StartsWith("mosaic://" + Host, StringComparison.OrdinalIgnoreCase) == true) return;
        model.Navigate("mosaic://" + Host);
    }
}
