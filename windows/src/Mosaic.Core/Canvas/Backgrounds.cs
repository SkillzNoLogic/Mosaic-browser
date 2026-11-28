using System.Text.Json.Nodes;
using Mosaic.Core.Canvas.Interim;

// Gradient Studio model, ported from macOS (Sources/MosaicCore/Canvas/Backgrounds.swift): the
// free-form ("mesh") gradient from Eminence, presets, conversions between kinds and the bounds
// the studio uses. A mesh is a set of colour sources, each pulling nearby pixels towards its
// colour by inverse-distance weighting, blended in OKLab. The data types themselves
// (GradientSpec, GradientSource, BackgroundSpec, NewTabBackground) and their JSON and clamping
// live in the shell's layout model. Owner: canvas.

namespace Mosaic.Core.Canvas;

/// <summary>Gradient Studio maths and helpers over the shell's gradient types.</summary>
public static class Gradients
{
    public static readonly (double Min, double Max) SharpnessRange = (1, 6);
    public static readonly (double Min, double Max) SpreadRange = (0.3, 3);
    public static readonly (double Min, double Max) OpacityRange = (0.1, 1);

    public static ColorValue ColorValue(this GradientSource source) => Canvas.ColorValue.FromHex(source.Color) ?? Canvas.ColorValue.Fallback;

    /// <summary>A new source with a fresh ID (an uppercase UUID, as on macOS).</summary>
    public static GradientSource NewSource(double x, double y, string color, double spread = 1) => new(null, x, y, color, spread);

    // MARK: Equality (the shell's types compare by reference; the canvas compares their JSON)

    private static string Key(JsonNode node) => SwiftJson.Write(node, pretty: false);

    /// <summary>The gradient's JSON in one line (a cache key; equal gradients give equal keys).</summary>
    public static string JsonKey(GradientSpec g) => Key(g.ToJson());

    /// <summary>A background's JSON in one line.</summary>
    public static string JsonKey(BackgroundSpec b) => Key(b.ToJson());

    public static bool Same(GradientSpec? a, GradientSpec? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && Key(a.ToJson()) == Key(b.ToJson()));

    public static bool Same(BackgroundSpec? a, BackgroundSpec? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && Key(a.ToJson()) == Key(b.ToJson()));

    public static bool Same(NewTabBackground? a, NewTabBackground? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && Key(a.ToJson()) == Key(b.ToJson()));

    public static bool SameWorkspaces(IReadOnlyDictionary<string, BackgroundSpec> a, IReadOnlyDictionary<string, BackgroundSpec> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && Same(p.Value, v));

    /// <summary>Layout equality for undo, "in use" and the like (the shell's <c>ContentEquals</c>).</summary>
    public static readonly IEqualityComparer<MosaicLayout> LayoutComparer = new LayoutEquality();

    private sealed class LayoutEquality : IEqualityComparer<MosaicLayout>
    {
        public bool Equals(MosaicLayout? x, MosaicLayout? y) => ReferenceEquals(x, y) || (x is not null && x.ContentEquals(y));
        public int GetHashCode(MosaicLayout obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>The gradient without its finish (grain, strength, Raw): what a preset match compares.</summary>
    public static bool SameDesign(this GradientSpec a, GradientSpec b)
    {
        var x = a.Clone();
        var y = b.Clone();
        x.Noise = y.Noise = 0;
        x.Opacity = y.Opacity = 1;
        x.Raw = y.Raw = false;
        return Same(x, y);
    }

    // MARK: Mesh

    private static double Power(GradientSpec g) => Math.Min(Math.Max(double.IsFinite(g.Sharpness) ? g.Sharpness : 2.2, 0.5), 8);

    /// <summary>The mesh colour at a point (0…1); <paramref name="aspect"/> is width ÷ height, so reach is round.</summary>
    public static ColorValue MeshColor(this GradientSpec g, double x, double y, double aspect)
    {
        double l = 0, a = 0, b = 0, total = 0;
        var power = Power(g);
        foreach (var source in g.MeshSources)
        {
            var d = double.Hypot((x - source.X) * aspect, y - source.Y) / Math.Max(source.Spread, 0.05);
            var weight = 1 / Math.Pow(d + 0.0005, power);
            var lab = source.ColorValue().Oklab;
            l += weight * lab.L;
            a += weight * lab.A;
            b += weight * lab.B;
            total += weight;
        }
        return Canvas.ColorValue.FromOklab(l / total, a / total, b / total);
    }

    /// <summary>The mesh as opaque RGBA pixels, <paramref name="requestedWidth"/> across (macOS <c>renderMesh</c>).</summary>
    public static (byte[] Pixels, int Width, int Height) RenderMesh(this GradientSpec g, int requestedWidth, double requestedAspect)
    {
        var (width, height, aspect) = GradientRaster.Size(requestedWidth, requestedAspect);
        var pixels = new byte[width * height * 4];
        GradientRaster.FillMesh(g, pixels, width, height, aspect, bgra: false);
        return (pixels, width, height);
    }

    /// <summary>The colour at <paramref name="location"/> (0…1) along the stops, interpolated in sRGB.</summary>
    public static ColorValue StopColor(this GradientSpec g, double location) => StopColor(g.NormalizedStops, location);

    internal static ColorValue StopColor(IReadOnlyList<GradientStop> stops, double location)
    {
        var t = Math.Min(Math.Max(double.IsFinite(location) ? location : 0, 0), 1);
        if (stops.Count == 0) return Canvas.ColorValue.Fallback;
        var first = stops[0];
        var last = stops[^1];
        if (t <= first.Location) return Canvas.ColorValue.FromHex(first.Color) ?? Canvas.ColorValue.Fallback;
        if (t >= last.Location) return Canvas.ColorValue.FromHex(last.Color) ?? Canvas.ColorValue.Fallback;
        for (var i = 0; i + 1 < stops.Count; i++)
        {
            var (sa, sb) = (stops[i], stops[i + 1]);
            if (t < sa.Location || t > sb.Location) continue;
            var ca = Canvas.ColorValue.FromHex(sa.Color) ?? Canvas.ColorValue.Fallback;
            var cb = Canvas.ColorValue.FromHex(sb.Color) ?? Canvas.ColorValue.Fallback;
            var span = sb.Location - sa.Location;
            var f = span > 0 ? (t - sa.Location) / span : 0;
            return new ColorValue(ca.R + (cb.R - ca.R) * f, ca.G + (cb.G - ca.G) * f, ca.B + (cb.B - ca.B) * f);
        }
        return Canvas.ColorValue.FromHex(last.Color) ?? Canvas.ColorValue.Fallback;
    }

    /// <summary>The overall colour, averaged in OKLab.</summary>
    public static ColorValue AverageColor(this GradientSpec g)
    {
        var values = g.Colors.Select(Canvas.ColorValue.FromHex).OfType<ColorValue>().ToList();
        if (values.Count == 0) return Canvas.ColorValue.Fallback;
        double l = 0, a = 0, b = 0;
        foreach (var v in values)
        {
            var lab = v.Oklab;
            l += lab.L;
            a += lab.A;
            b += lab.B;
        }
        return Canvas.ColorValue.FromOklab(l / values.Count, a / values.Count, b / values.Count);
    }

    /// <summary>
    /// The same colours as another kind (switching kinds keeps the look): stops become sources
    /// laid along the angle; sources become stops by where they fall along it.
    /// </summary>
    public static GradientSpec Converted(this GradientSpec self, GradientKind newKind)
    {
        var g = self.Clone();
        if (newKind == self.Kind) return g;
        g.Kind = newKind;
        var radians = self.Angle * Math.PI / 180;
        double dx = Math.Cos(radians), dy = Math.Sin(radians);
        if (newKind == GradientKind.Mesh && self.Kind != GradientKind.Mesh)
        {
            g.Sources = [.. self.NormalizedStops.Select((stop, index) =>
            {
                var offset = stop.Location - 0.5;
                return new GradientSource($"s{index}-{Guid.NewGuid().ToString().ToUpperInvariant()[..8]}",
                    Math.Min(Math.Max(0.5 + dx * offset, 0), 1), Math.Min(Math.Max(0.5 + dy * offset, 0), 1), stop.Color);
            })];
        }
        else if (self.Kind == GradientKind.Mesh && newKind != GradientKind.Mesh)
        {
            var projected = self.MeshSources.Select(s => (P: (s.X - 0.5) * dx + (s.Y - 0.5) * dy, s.Color))
                .OrderBy(p => p.P).ToList();
            var lo = projected.Count > 0 ? projected[0].P : 0;
            var hi = projected.Count > 0 ? projected[^1].P : 1;
            var span = hi - lo;
            g.Stops = [.. projected.Select((item, index) => new GradientStop(item.Color,
                span > 0.0001 ? (item.P - lo) / span : (double)index / Math.Max(projected.Count - 1, 1)))];
        }
        return g;
    }
}

/// <summary>Ready-made gradients: Eminence's free-form designs and the shell's soft linear backgrounds.</summary>
public static class GradientPresets
{
    private static (string Name, Func<GradientSpec> Make) Mesh(string name, (double X, double Y, string Color)[] points, double sharpness = 2.2)
    {
        var slug = name.ToLowerInvariant().Replace(" ", "-");
        return (name, () => new GradientSpec
        {
            Kind = GradientKind.Mesh,
            Angle = 135,
            Sources = [.. points.Select((p, i) => new GradientSource($"{slug}-{i}", p.X, p.Y, p.Color))],
            Sharpness = sharpness,
        });
    }

    private static (string Name, Func<GradientSpec> Make) Linear(string name, double angle, (string Color, double Location)[] stops) =>
        (name, () => new GradientSpec(GradientKind.Linear, angle, [.. stops.Select(s => new GradientStop(s.Color, s.Location))]));

    private static readonly (string Name, Func<GradientSpec> Make)[] mesh =
    [
        Mesh("Sunset", [(0.05, 0.1, "#FF7E5F"), (0.95, 0.15, "#FEB47B"), (0.5, 0.95, "#6A3093")]),
        Mesh("Aurora", [(0.1, 0.9, "#00C9A7"), (0.9, 0.1, "#845EC2"), (0.35, 0.3, "#2C73D2"), (0.75, 0.75, "#0081CF")]),
        Mesh("Ocean", [(0.5, 0.0, "#2E3192"), (0.5, 1.0, "#1BFFFF")], 1.6),
        Mesh("Peach", [(0.0, 0.0, "#FFD3A5"), (1.0, 1.0, "#FD6585")], 1.6),
        Mesh("Four Corners", [(0, 0, "#FF6B6B"), (1, 0, "#FFD93D"), (0, 1, "#4D96FF"), (1, 1, "#6BCB77")]),
        Mesh("Radial Glow", [(0.5, 0.5, "#F2994A"), (0, 0, "#1A1A2E"), (1, 0, "#1A1A2E"), (0, 1, "#1A1A2E"), (1, 1, "#1A1A2E")], 2.6),
        Mesh("Diagonal", [(0.0, 0.0, "#667EEA"), (1.0, 1.0, "#764BA2")], 1.6),
        Mesh("Duotone", [(0.0, 0.5, "#FC466B"), (1.0, 0.5, "#3F5EFB")], 1.8),
        Mesh("Midnight", [(0.1, 0.1, "#0F2027"), (0.6, 0.5, "#203A43"), (0.95, 0.95, "#2C5364")]),
        Mesh("Pastel", [(0.15, 0.2, "#FBC2EB"), (0.85, 0.15, "#A6C1EE"), (0.5, 0.55, "#FDFCFB"), (0.2, 0.9, "#C2E9FB"), (0.85, 0.85, "#FEE140")]),
        Mesh("Mesh", [(0.15, 0.2, "#7F7FD5"), (0.8, 0.1, "#86A8E7"), (0.45, 0.55, "#91EAE4"), (0.1, 0.85, "#F8B195"), (0.9, 0.8, "#C06C84")], 2.8),
        Mesh("Forest", [(0.0, 0.0, "#134E5E"), (1.0, 0.3, "#71B280"), (0.3, 1.0, "#0B3D2E")]),
    ];

    private static readonly (string Name, Func<GradientSpec> Make)[] linear =
    [
        Linear("Lavender", 125, [("#C9BFF7", 0), ("#E7D6F5", 0.55), ("#BFD3F5", 1)]),
        Linear("Dusk", 135, [("#FFB88C", 0), ("#FF8FA3", 0.5), ("#C79BFF", 1)]),
        Linear("Sky", 140, [("#A1C4FD", 0), ("#C2E9FB", 1)]),
        Linear("Mint", 120, [("#B8F1D3", 0), ("#D9F6C4", 1)]),
        Linear("Night", 160, [("#1B1024", 0), ("#0F0E15", 0.6), ("#24101A", 1)]),
        Linear("Graphite", 90, [("#3A3D44", 0), ("#24262B", 1)]),
    ];

    /// <summary>Fresh copies, so callers may change what they get.</summary>
    public static IReadOnlyList<(string Name, GradientSpec Gradient)> MeshPresets => [.. mesh.Select(p => (p.Name, p.Make()))];

    public static IReadOnlyList<(string Name, GradientSpec Gradient)> LinearPresets => [.. linear.Select(p => (p.Name, p.Make()))];

    public static IReadOnlyList<(string Name, GradientSpec Gradient)> All => [.. MeshPresets, .. LinearPresets];

    public static IReadOnlyList<string> Names => [.. mesh.Select(p => p.Name), .. linear.Select(p => p.Name)];

    /// <summary>A preset by name, ignoring case and spaces.</summary>
    public static GradientSpec? Named(string name)
    {
        static string Key(string s) => s.ToLowerInvariant().Replace(" ", "");
        var match = mesh.Concat(linear).FirstOrDefault(p => Key(p.Name) == Key(name));
        return match.Make?.Invoke();
    }
}
