using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mosaic.App.Features.Productivity;

/// <summary>Brushes and helpers shared by the area's views (theme resources from XamlControlsResources, with fallbacks).</summary>
internal static class ProductivityStyle
{
    /// <summary>A theme brush by its Fluent resource key, or <paramref name="fallback"/> when it is missing.</summary>
    public static Brush Resource(string key, Color fallback)
    {
        if (Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }

    /// <summary>Acrylic for transient surfaces; the resource falls back to a solid colour when transparency is off.</summary>
    public static Brush TransientSurface => Resource("AcrylicInAppFillColorDefaultBrush", Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B));

    public static Brush Card => Resource("CardBackgroundFillColorDefaultBrush", Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));

    public static Brush CardStroke => Resource("CardStrokeColorDefaultBrush", Color.FromArgb(0x19, 0x00, 0x00, 0x00));

    public static Brush SecondaryText => Resource("TextFillColorSecondaryBrush", Colors.Gray);

    public static Brush TertiaryText => Resource("TextFillColorTertiaryBrush", Colors.DarkGray);

    public static Brush Accent => Resource("AccentFillColorDefaultBrush", Color.FromArgb(0xFF, 0x00, 0x67, 0xC0));

    public static Brush Critical => Resource("SystemFillColorCriticalBrush", Colors.IndianRed);

    public static Brush Caution => Resource("SystemFillColorCautionBrush", Colors.Goldenrod);

    public static Brush Subtle => Resource("SubtleFillColorSecondaryBrush", Color.FromArgb(0x0F, 0x80, 0x80, 0x80));

    public static Brush Smoke => Resource("SmokeFillColorDefaultBrush", Color.FromArgb(0x4D, 0x00, 0x00, 0x00));

    /// <summary>Overlay corner radius on Windows 11.</summary>
    public static readonly CornerRadius OverlayCorners = new(8);

    public static readonly CornerRadius ControlCorners = new(4);
}
