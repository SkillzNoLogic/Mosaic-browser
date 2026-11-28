using System.Reflection;
using Microsoft.UI.Xaml;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// The winui area's window API (<c>MainWindow.Material</c>, <c>CaptionButtonsWidth</c>,
/// <c>SetInteractiveTitleBarElements</c>, <c>IsBackdropTransparent</c>), used when the window has
/// it, so the shell and the window agree on the backdrop and the title bar. Against the
/// integrator's skeleton window, which has none of it, the shell does the job itself.
/// </summary>
/// <remarks>
/// TODO(shell): call <c>MainWindow</c> directly once winui's milestone M1 is merged into win-next.
/// </remarks>
internal sealed class WindowApi(Window window)
{
    private readonly Type type = window.GetType();

    private PropertyInfo? Property(string name) => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    public bool HasMaterial => Property("Material") is { CanWrite: true, PropertyType.IsEnum: true };

    /// <summary>Asks the window for "Mica", "MicaAlt", "Acrylic" or "Solid"; false when it cannot.</summary>
    public bool SetMaterial(string name)
    {
        if (Property("Material") is not { CanWrite: true } property || !property.PropertyType.IsEnum) return false;
        if (!Enum.TryParse(property.PropertyType, name, out var value)) return false;
        property.SetValue(window, value);
        return true;
    }

    /// <summary>The window's backdrop shows through (null when the window does not say).</summary>
    public bool? IsBackdropTransparent => Property("IsBackdropTransparent")?.GetValue(window) as bool?;

    public string? EffectiveMaterial => Property("EffectiveMaterial")?.GetValue(window)?.ToString();

    public double? CaptionButtonsWidth => Property("CaptionButtonsWidth")?.GetValue(window) as double?;

    public bool SetInteractiveTitleBarElements(FrameworkElement[] elements)
    {
        var method = type.GetMethod("SetInteractiveTitleBarElements", BindingFlags.Public | BindingFlags.Instance, [typeof(FrameworkElement[])]);
        if (method is null) return false;
        method.Invoke(window, [elements]);
        return true;
    }

    /// <summary>Raised when the window's effective material changes (no-op on the skeleton window).</summary>
    public void OnMaterialChanged(Action handler)
    {
        if (type.GetEvent("MaterialChanged") is { } e && e.EventHandlerType == typeof(Action)) e.AddEventHandler(window, handler);
    }
}
