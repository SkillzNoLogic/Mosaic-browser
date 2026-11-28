using Microsoft.UI.Xaml;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// The window's API the shell uses (winui area's <see cref="MainWindow"/>): the backdrop
/// material and its fallbacks, the caption buttons' width and the title bar's interactive
/// regions. Any other <see cref="Window"/> (tests, a future second kind of window) gets the
/// shell's own fallbacks.
/// </summary>
internal sealed class WindowApi(Window window)
{
    private readonly MainWindow? main = window as MainWindow;

    public bool HasMaterial => main is not null;

    /// <summary>Asks the window for "Mica", "MicaAlt", "Acrylic" or "Solid"; false when it cannot.</summary>
    public bool SetMaterial(string name)
    {
        if (main is null || !Enum.TryParse<WindowMaterial>(name, out var material)) return false;
        main.Material = material;
        return true;
    }

    /// <summary>The window's backdrop shows through (null when the window does not say).</summary>
    public bool? IsBackdropTransparent => main?.IsBackdropTransparent;

    public string? EffectiveMaterial => main?.EffectiveMaterial.ToString();

    public double? CaptionButtonsWidth => main?.CaptionButtonsWidth;

    public bool SetInteractiveTitleBarElements(FrameworkElement[] elements)
    {
        if (main is null) return false;
        main.SetInteractiveTitleBarElements(elements);
        return true;
    }

    /// <summary>Raised when the window's effective material changes.</summary>
    public void OnMaterialChanged(Action handler)
    {
        if (main is not null) main.MaterialChanged += handler;
    }
}
