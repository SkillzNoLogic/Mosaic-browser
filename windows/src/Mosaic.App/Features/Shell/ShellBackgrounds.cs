using Microsoft.UI.Xaml.Media;
using Mosaic.Core.Shell;
using Windows.Foundation;
using Windows.UI;

using XamlGradientStop = Microsoft.UI.Xaml.Media.GradientStop;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Paints a Mosaic's window background (port of macOS's <c>BackgroundFill</c>). Linear stops are
/// drawn exactly; radial, angular and the Gradient Studio's mesh are approximated until the canvas
/// area sets <see cref="Renderer"/>. A light colour is dimmed in dark mode (and a dark one lifted
/// in light mode) so the chrome stays legible, unless the gradient is "Raw".
/// </summary>
public static class ShellBackgrounds
{
    /// <summary>The canvas area's renderer (mesh, grain, strength): returns null to leave a spec to the shell.</summary>
    public static Func<BackgroundSpec, bool, Brush?>? Renderer { get; set; }

    public static Brush? Fill(BackgroundSpec spec, bool dark, bool translucent)
    {
        switch (spec.Kind)
        {
            case BackgroundKind.Solid:
                var color = ShellTheme.ColorFromHex(spec.Color) ?? ShellTheme.C(0xF2, 0xF2, 0xF7);
                return new SolidColorBrush(Adapt(color, HexColor.Luminance([spec.Color]), dark));
            case BackgroundKind.Gradient:
                return Gradient(spec.Gradient, dark, translucent);
            default:
                return null;
        }
    }

    public static Brush Gradient(GradientSpec spec, bool dark, bool translucent = false)
    {
        var luminance = HexColor.Luminance(spec.Colors);
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        // 0° runs left to right, 90° top to bottom.
        var radians = spec.Angle * Math.PI / 180;
        var dx = Math.Cos(radians) / 2;
        var dy = Math.Sin(radians) / 2;
        brush.StartPoint = new Point(0.5 - dx, 0.5 - dy);
        brush.EndPoint = new Point(0.5 + dx, 0.5 + dy);
        IEnumerable<(string Color, double Location)> stops = spec.Kind == GradientKind.Mesh
            ? spec.MeshSources.OrderBy(s => s.X + s.Y).Select((s, i, n) => (s.Color, n <= 1 ? 0 : (double)i / (n - 1)))
            : spec.NormalizedStops.Select(s => (s.Color, s.Location));
        foreach (var (hex, location) in stops)
        {
            var color = ShellTheme.ColorFromHex(hex) ?? ShellTheme.C(0x80, 0x80, 0x80);
            if (!spec.Raw) color = Adapt(color, luminance, dark);
            color = ShellTheme.WithAlpha(color, Math.Clamp(spec.Opacity, 0.1, 1) * (color.A / 255.0));
            brush.GradientStops.Add(new XamlGradientStop { Color = color, Offset = location });
        }
        return brush;
    }

    private static Color Adapt(Color color, double luminance, bool dark)
    {
        if (dark && luminance > 0.55) return ShellTheme.Mix(color, ShellTheme.C(0, 0, 0), 0.66);
        if (!dark && luminance < 0.35) return ShellTheme.Mix(color, ShellTheme.C(0xFF, 0xFF, 0xFF), 0.62);
        return color;
    }

    private static IEnumerable<TResult> Select<T, TResult>(this IEnumerable<T> source, Func<T, int, int, TResult> selector)
    {
        var list = source.ToList();
        for (var i = 0; i < list.Count; i++) yield return selector(list[i], i, list.Count);
    }
}
