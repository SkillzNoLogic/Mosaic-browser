using Mosaic.Core.Canvas.Interim;

// CPU rasterising of Gradient Studio gradients, for the WinUI app. WinUI has no conic brush,
// no free-form gradient and no tiling image brush, so every kind is drawn into a small pixel
// buffer on a background thread and scaled up smoothly by the compositor (a gradient has no
// fine detail to lose). Rendering stays well under a millisecond per frame at these sizes,
// keeps all four kinds pixel-consistent with each other, and needs no native dependency
// (Win2D would add one per architecture and load time, for no visible gain here).
// The geometry matches SwiftUI's on macOS (GradientRendering.swift). Owner: canvas.

namespace Mosaic.Core.Canvas;

public static class GradientRaster
{
    /// <summary>Pixels across for window backgrounds (as macOS: 160, scaled up smoothly).</summary>
    public const int BackgroundWidth = 160;

    /// <summary>Pixels across for preset thumbnails.</summary>
    public const int ThumbnailWidth = 64;

    private static int renderCount;

    /// <summary>How many gradients were rasterised (for <c>dump</c>: it must stay put while idle).</summary>
    public static int RenderCount => Volatile.Read(ref renderCount);

    /// <summary>Clamped size for a request (width 1…1024, aspect 0.1…10, height 1…1024).</summary>
    public static (int Width, int Height, double Aspect) Size(int requestedWidth, double requestedAspect)
    {
        var width = Math.Min(Math.Max(requestedWidth, 1), 1024);
        var aspect = double.IsFinite(requestedAspect) ? Math.Min(Math.Max(requestedAspect, 0.1), 10) : 1.6;
        var height = Math.Min(Math.Max(1, (int)Math.Round(width / aspect, MidpointRounding.AwayFromZero)), 1024);
        return (width, height, aspect);
    }

    /// <summary>
    /// The gradient (any kind, without strength/grain/adaptation) as opaque pixels:
    /// BGRA (premultiplied; opaque, so the same) for WinUI bitmaps, or RGBA.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Render(GradientSpec gradient, int requestedWidth, double requestedAspect, bool bgra = true)
    {
        var (width, height, aspect) = Size(requestedWidth, requestedAspect);
        var pixels = new byte[width * height * 4];
        if (gradient.Kind == GradientKind.Mesh) FillMesh(gradient, pixels, width, height, aspect, bgra);
        else FillStops(gradient, pixels, width, height, bgra);
        Interlocked.Increment(ref renderCount);
        return (pixels, width, height);
    }

    private static void Put(byte[] pixels, int offset, ColorValue c, bool bgra)
    {
        byte r = (byte)Math.Round(c.R * 255, MidpointRounding.AwayFromZero);
        byte g = (byte)Math.Round(c.G * 255, MidpointRounding.AwayFromZero);
        byte b = (byte)Math.Round(c.B * 255, MidpointRounding.AwayFromZero);
        pixels[offset] = bgra ? b : r;
        pixels[offset + 1] = g;
        pixels[offset + 2] = bgra ? r : b;
        pixels[offset + 3] = 255;
    }

    internal static void FillMesh(GradientSpec g, byte[] pixels, int width, int height, double aspect, bool bgra)
    {
        var sources = g.MeshSources;
        var labs = sources.Select(s => s.ColorValue().Oklab).ToArray();
        var points = sources.Select(s => (s.X, s.Y, Inverse: 1 / Math.Max(s.Spread, 0.05))).ToArray();
        var power = Math.Min(Math.Max(double.IsFinite(g.Sharpness) ? g.Sharpness : 2.2, 0.5), 8);
        for (var py = 0; py < height; py++)
        {
            var y = (py + 0.5) / height;
            for (var px = 0; px < width; px++)
            {
                var x = (px + 0.5) / width;
                double l = 0, a = 0, b = 0, total = 0;
                for (var i = 0; i < points.Length; i++)
                {
                    var d = double.Hypot((x - points[i].X) * aspect, y - points[i].Y) * points[i].Inverse;
                    var weight = 1 / Math.Pow(d + 0.0005, power);
                    l += weight * labs[i].L;
                    a += weight * labs[i].A;
                    b += weight * labs[i].B;
                    total += weight;
                }
                Put(pixels, (py * width + px) * 4, ColorValue.FromOklab(l / total, a / total, b / total), bgra);
            }
        }
    }

    /// <summary>Linear, radial and angular, with SwiftUI's geometry (in pixel space).</summary>
    private static void FillStops(GradientSpec g, byte[] pixels, int width, int height, bool bgra)
    {
        var stops = g.NormalizedStops;
        // A lookup table along t keeps the per-pixel work to a multiply.
        const int steps = 1024;
        var table = new ColorValue[steps + 1];
        for (var i = 0; i <= steps; i++) table[i] = Gradients.StopColor(stops, (double)i / steps);
        double w = width, h = height;
        var radians = g.Angle * Math.PI / 180;
        // Linear: from (0.5 − cos/2, 0.5 − sin/2) to (0.5 + cos/2, 0.5 + sin/2) of the frame.
        double sx = (0.5 - Math.Cos(radians) / 2) * w, sy = (0.5 - Math.Sin(radians) / 2) * h;
        double ex = (0.5 + Math.Cos(radians) / 2) * w, ey = (0.5 + Math.Sin(radians) / 2) * h;
        double vx = ex - sx, vy = ey - sy, length2 = Math.Max(vx * vx + vy * vy, 1e-9);
        double cx = g.CenterX * w, cy = g.CenterY * h;
        var radius = Math.Max(Math.Max(w, h) * 0.75, 1e-9);
        var start = double.IsFinite(g.Angle) ? g.Angle : 0;
        for (var py = 0; py < height; py++)
        {
            var y = py + 0.5;
            for (var px = 0; px < width; px++)
            {
                var x = px + 0.5;
                double t;
                switch (g.Kind)
                {
                    case GradientKind.Radial:
                        t = double.Hypot(x - cx, y - cy) / radius;
                        break;
                    case GradientKind.Angular:
                        var theta = Math.Atan2(y - cy, x - cx) * 180 / Math.PI;  // clockwise from +x (y down)
                        t = (((theta - start) % 360) + 360) % 360 / 360;
                        break;
                    default:
                        t = ((x - sx) * vx + (y - sy) * vy) / length2;
                        break;
                }
                var index = (int)Math.Round(Math.Min(Math.Max(t, 0), 1) * steps);
                Put(pixels, (py * width + px) * 4, table[index], bgra);
            }
        }
    }

    /// <summary>
    /// Film grain: one tile of noise made once (macOS draws the same noise with an overlay blend;
    /// WinUI has no blend modes, so the tile carries it as light and dark specks around mid-grey,
    /// which reads the same). BGRA, premultiplied.
    /// </summary>
    public static (byte[] Pixels, int Side) GrainTile(int side = 128)
    {
        var pixels = new byte[side * side * 4];
        var seed = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < side * side; i++)
        {
            seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL);
            var v = (int)(seed >> 56);
            var white = v >= 128;
            var alpha = white ? (v - 128) * 255 / 127 : (128 - v) * 255 / 128;
            var colour = white ? (byte)alpha : (byte)0;  // premultiplied
            pixels[i * 4] = colour;
            pixels[i * 4 + 1] = colour;
            pixels[i * 4 + 2] = colour;
            pixels[i * 4 + 3] = (byte)alpha;
        }
        return (pixels, side);
    }

    /// <summary>
    /// How the window shows a gradient unless Raw: dimmed with black in dark mode when light,
    /// lifted with white in light mode when dark (macOS CanvasGradientBackground). Returns the
    /// overlay colour (true = white) and its opacity, or null.
    /// </summary>
    public static (bool White, double Opacity)? Adaptation(GradientSpec g, bool darkMode)
    {
        if (g.Raw) return null;
        var luminance = HexColor.Luminance(g.Colors);
        if (darkMode && luminance > 0.55) return (false, 0.66);
        if (!darkMode && luminance < 0.35) return (true, 0.62);
        return null;
    }
}
