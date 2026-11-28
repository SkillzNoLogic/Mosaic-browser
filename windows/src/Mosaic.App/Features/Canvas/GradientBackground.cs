using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.Core.Canvas;
using Mosaic.Core.Canvas.Interim;
using Windows.UI;

namespace Mosaic.App.Features.Canvas;

/// <summary>
/// Rendered gradients, keyed by what affects the pixels (the design, the width and the shape to
/// 1/50), so redrawing a window or a thumbnail reuses them. Pixels are computed on the thread
/// pool; bitmaps are made on the UI thread. Bounded (a drag makes many one-off frames). Nothing
/// is rendered until a gradient is first shown, so start-up does no gradient work.
/// </summary>
internal static class GradientImageCache
{
    private const int Capacity = 48;
    private static readonly Dictionary<string, WriteableBitmap> images = [];
    private static readonly LinkedList<string> order = [];
    private static readonly Dictionary<string, Task<(byte[] Pixels, int Width, int Height)>> pending = [];

    /// <summary>The gradient with only what changes its pixels (finish and unused fields reset).</summary>
    private static GradientSpec Shape(GradientSpec gradient)
    {
        var shape = gradient.Clone();
        shape.Noise = 0;
        shape.Opacity = 1;
        shape.Raw = false;
        if (shape.Kind == GradientKind.Mesh)
        {
            shape.Angle = 0;
            shape.Stops = [];
            shape.CenterX = shape.CenterY = 0.5;
        }
        else
        {
            shape.Sources = [];
            shape.Sharpness = 2.2;
        }
        return shape;
    }

    private static string Key(GradientSpec shape, int width, int aspectBucket) =>
        $"{width}:{aspectBucket}:{Gradients.JsonKey(shape)}";

    public static WriteableBitmap? Cached(GradientSpec gradient, int width, double aspect, out string key, out GradientSpec shape, out double bucketAspect)
    {
        shape = Shape(gradient);
        var bucket = (int)Math.Round((double.IsFinite(aspect) && aspect > 0 ? aspect : 1.6) * 50);
        bucket = Math.Max(bucket, 1);
        bucketAspect = bucket / 50.0;
        key = Key(shape, width, bucket);
        if (!images.TryGetValue(key, out var image)) return null;
        order.Remove(key);
        order.AddFirst(key);
        return image;
    }

    /// <summary>The image, from the cache or rendered off the UI thread (call on the UI thread).</summary>
    public static async Task<WriteableBitmap?> GetAsync(GradientSpec gradient, int width, double aspect)
    {
        if (Cached(gradient, width, aspect, out var key, out var shape, out var bucketAspect) is { } hit) return hit;
        if (!pending.TryGetValue(key, out var task))
        {
            task = Task.Run(() => GradientRaster.Render(shape, width, bucketAspect));
            pending[key] = task;
        }
        (byte[] Pixels, int Width, int Height) rendered;
        try { rendered = await task; }
        finally { pending.Remove(key); }
        if (images.TryGetValue(key, out var raced)) return raced;
        var bitmap = new WriteableBitmap(rendered.Width, rendered.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(rendered.Pixels, 0, rendered.Pixels.Length);
        bitmap.Invalidate();
        images[key] = bitmap;
        order.AddFirst(key);
        while (order.Count > Capacity)
        {
            images.Remove(order.Last!.Value);
            order.RemoveLast();
        }
        return bitmap;
    }

    private static WriteableBitmap? grain;

    /// <summary>Film grain: one 128-pixel tile, made once.</summary>
    public static WriteableBitmap Grain
    {
        get
        {
            if (grain is not null) return grain;
            var (pixels, side) = GradientRaster.GrainTile();
            grain = new WriteableBitmap(side, side);
            using (var stream = grain.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
            grain.Invalidate();
            return grain;
        }
    }
}

/// <summary>
/// A gradient exactly as specified (any kind), filling its size: no strength, grain or
/// adaptation. Used for thumbnails, the stop bar and inside <see cref="GradientBackground"/>.
/// </summary>
internal sealed class GradientFill : Grid
{
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private GradientSpec gradient;
    private readonly int resolution;
    private int version;

    public GradientFill(GradientSpec gradient, int resolution = GradientRaster.BackgroundWidth)
    {
        this.gradient = gradient;
        this.resolution = resolution;
        Children.Add(image);
        SizeChanged += (_, _) => Render();
        Loaded += (_, _) => Render();
        IsHitTestVisible = false;
    }

    public GradientSpec Gradient
    {
        get => gradient;
        set
        {
            gradient = value;
            Render();
        }
    }

    private double Aspect => ActualHeight > 0 && ActualWidth > 0 ? ActualWidth / ActualHeight : 1.6;

    private async void Render()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var mine = ++version;
        var aspect = Aspect;
        if (GradientImageCache.Cached(gradient, resolution, aspect, out _, out _, out _) is { } hit)
        {
            image.Source = hit;
            return;
        }
        var bitmap = await GradientImageCache.GetAsync(gradient, resolution, aspect);
        if (mine == version) image.Source = bitmap;  // a newer gradient arrived meanwhile: keep that one
    }
}

/// <summary>
/// A background as the window shows it (macOS BackgroundFill + CanvasGradientBackground): the
/// system colour, a solid colour, or a gradient at its strength over the system colour, with
/// grain, and, unless Raw, dimmed in dark mode or lifted in light mode so the chrome stays
/// legible. For the shell's window, New Tab page and per-workspace backgrounds:
/// <c>new GradientBackground(layout.Appearance.BackgroundForWorkspace(id))</c>, then set
/// <see cref="Spec"/> when the layout changes. Nothing animates: an idle window does no work.
/// </summary>
public sealed class GradientBackground : Grid
{
    private readonly Grid baseLayer = new();
    private readonly GradientFill fill;
    private readonly Grid adaptation = new() { IsHitTestVisible = false };
    private readonly Microsoft.UI.Xaml.Controls.Canvas grainLayer = new() { IsHitTestVisible = false };
    private BackgroundSpec spec;

    public GradientBackground(BackgroundSpec spec, int resolution = GradientRaster.BackgroundWidth)
    {
        this.spec = spec;
        fill = new GradientFill(spec.Gradient, resolution);
        Children.Add(baseLayer);
        Children.Add(fill);
        Children.Add(adaptation);
        Children.Add(grainLayer);
        IsHitTestVisible = false;
        SizeChanged += (_, _) => LayoutGrain();
        ActualThemeChanged += (_, _) => Apply();
        Apply();
    }

    public BackgroundSpec Spec
    {
        get => spec;
        set
        {
            if (Gradients.Same(spec, value)) return;
            spec = value;
            Apply();
        }
    }

    private static SolidColorBrush Solid(string hex, byte alpha = 255)
    {
        var c = HexColor.Components(hex) ?? (0.5, 0.5, 0.5, 1);
        return new SolidColorBrush(Color.FromArgb(alpha, (byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255)));
    }

    private Brush SystemBackground =>
        Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var b) && b is Brush brush
            ? brush : new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));

    private void Apply()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        adaptation.Background = null;
        switch (spec.Kind)
        {
            case BackgroundKind.System:
                baseLayer.Background = SystemBackground;
                fill.Visibility = Visibility.Collapsed;
                grainLayer.Visibility = Visibility.Collapsed;
                break;
            case BackgroundKind.Solid:
                baseLayer.Background = Solid(spec.Color);
                fill.Visibility = Visibility.Collapsed;
                grainLayer.Visibility = Visibility.Collapsed;
                var luminance = HexColor.Luminance([spec.Color]);
                if (dark && luminance > 0.55) adaptation.Background = new SolidColorBrush(Color.FromArgb((byte)(0.66 * 255), 0, 0, 0));
                else if (!dark && luminance < 0.35) adaptation.Background = new SolidColorBrush(Color.FromArgb((byte)(0.62 * 255), 255, 255, 255));
                break;
            default:
                var g = spec.Gradient;
                baseLayer.Background = g.Opacity < 0.999 ? SystemBackground : null;
                fill.Visibility = Visibility.Visible;
                fill.Opacity = Math.Clamp(g.Opacity, 0.1, 1);
                fill.Gradient = g;
                if (GradientRaster.Adaptation(g, dark) is { } a)
                    adaptation.Background = new SolidColorBrush(a.White ? Color.FromArgb((byte)(a.Opacity * 255), 255, 255, 255) : Color.FromArgb((byte)(a.Opacity * 255), 0, 0, 0));
                grainLayer.Visibility = g.Noise > 0.001 ? Visibility.Visible : Visibility.Collapsed;
                grainLayer.Opacity = Math.Min(g.Noise, 1) * 0.55;
                LayoutGrain();
                break;
        }
    }

    /// <summary>Tiles the one grain bitmap (WinUI has no tiling brush): one Image per 128×128 DIPs, sharing the bitmap.</summary>
    private void LayoutGrain()
    {
        if (grainLayer.Visibility != Visibility.Visible || ActualWidth <= 0) return;
        const double side = 128;
        var columns = (int)Math.Ceiling(ActualWidth / side);
        var rows = (int)Math.Ceiling(ActualHeight / side);
        var needed = columns * rows;
        while (grainLayer.Children.Count < needed) grainLayer.Children.Add(new Image { Source = GradientImageCache.Grain, Width = side, Height = side, Stretch = Stretch.Fill });
        while (grainLayer.Children.Count > needed) grainLayer.Children.RemoveAt(grainLayer.Children.Count - 1);
        for (var i = 0; i < needed; i++)
        {
            var tile = grainLayer.Children[i];
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(tile, i % columns * side);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(tile, i / columns * side);
        }
        grainLayer.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight) };
    }
}
