using System.Text.Json.Nodes;
using Mosaic.Core.Canvas.Interim;

// Gradient Studio model, ported from macOS (Sources/MosaicCore/Canvas/Backgrounds.swift): the
// free-form ("mesh") gradient from Eminence, its colour sources, presets, conversions between
// kinds, the bounds every imported or edited gradient is held to, and the New Tab page's
// background. A mesh is a set of colour sources, each pulling nearby pixels towards its colour
// by inverse-distance weighting, blended in OKLab. Owner: canvas.

namespace Mosaic.Core.Canvas;

/// <summary>One colour source of a mesh gradient (position 0…1 of the window, colour, reach).</summary>
public sealed record GradientSource(string Id, double X, double Y, string Color, double Spread = 1)
{
    public static GradientSource Make(double x, double y, string color, double spread = 1) =>
        new(NewId(), x, y, color, spread);

    public ColorValue ColorValue => ColorValue.FromHex(Color) ?? ColorValue.Fallback;

    /// <summary>An uppercase UUID string, as Swift's <c>UUID().uuidString</c>.</summary>
    public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();
}

/// <summary>The New Tab page's background.</summary>
public sealed class NewTabBackground : IEquatable<NewTabBackground>
{
    public enum Mode { Plain, Window, Custom }

    public Mode BackgroundMode { get; set; } = Mode.Plain;
    public BackgroundSpec Custom { get; set; } = new(BackgroundSpec.Kind.Gradient, gradient: GradientPresets.All[0].Gradient);

    public NewTabBackground Clone() => new() { BackgroundMode = BackgroundMode, Custom = Custom.Clone() };

    public static string Title(Mode mode) => mode switch
    {
        Mode.Plain => "Plain",
        Mode.Window => "Same as Window",
        _ => "Own Background",
    };

    public bool Equals(NewTabBackground? o) => o is not null && BackgroundMode == o.BackgroundMode && Custom == o.Custom;
    public override bool Equals(object? obj) => Equals(obj as NewTabBackground);
    public override int GetHashCode() => HashCode.Combine(BackgroundMode, Custom);
    public static bool operator ==(NewTabBackground? a, NewTabBackground? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(NewTabBackground? a, NewTabBackground? b) => !(a == b);
}

/// <summary>JSON of the canvas-owned types, as the macOS app writes them.</summary>
public static class CanvasJson
{
    public static JsonObject Encode(GradientSource s) => new()
    {
        ["color"] = s.Color,
        ["id"] = s.Id,
        ["spread"] = LayoutJson.Number(s.Spread),
        ["x"] = LayoutJson.Number(s.X),
        ["y"] = LayoutJson.Number(s.Y),
    };

    /// <summary>Forgiving: missing values get defaults; Eminence's <c>hex</c> key is accepted for <c>color</c>.</summary>
    public static GradientSource? DecodeSource(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new GradientSource(
            LayoutJson.String(o, "id") ?? GradientSource.NewId(),
            LayoutJson.Double(o, "x") ?? 0.5,
            LayoutJson.Double(o, "y") ?? 0.5,
            LayoutJson.String(o, "color") ?? LayoutJson.String(o, "hex") ?? "#808080",
            LayoutJson.Double(o, "spread") ?? 1);
    }

    public static JsonObject Encode(NewTabBackground b) => new()
    {
        ["custom"] = LayoutJson.Encode(b.Custom),
        ["mode"] = b.BackgroundMode.Raw(),
    };

    public static NewTabBackground? DecodeNewTab(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var d = new NewTabBackground();
        return new NewTabBackground
        {
            BackgroundMode = LayoutJson.Enum<NewTabBackground.Mode>(o, "mode") ?? d.BackgroundMode,
            Custom = LayoutJson.DecodeBackground(o["custom"]) ?? d.Custom,
        };
    }

    public static string Write(GradientSpec gradient) => LayoutJson.Write(LayoutJson.Encode(gradient));

    /// <summary>A gradient file (a GradientSpec as JSON), or null when it isn't one.</summary>
    public static GradientSpec? ReadGradient(string json)
    {
        try { return LayoutJson.DecodeGradient(JsonNode.Parse(json)); }
        catch (Exception) { return null; }
    }
}

/// <summary>Gradient Studio bounds, mesh maths and conversions.</summary>
public static class Gradients
{
    public const int MaxStops = 16;
    public const int MaxSources = 32;
    public static readonly (double Min, double Max) SharpnessRange = (1, 6);
    public static readonly (double Min, double Max) SpreadRange = (0.3, 3);
    public static readonly (double Min, double Max) OpacityRange = (0.1, 1);

    private static double Clamp(double v, (double Min, double Max) range, double fallback) =>
        double.IsFinite(v) ? Math.Min(Math.Max(v, range.Min), range.Max) : fallback;

    /// <summary>Every number in range, invalid colours dropped, lists capped. Valid values are left exactly as they are.</summary>
    public static GradientSpec Clamped(this GradientSpec self)
    {
        var g = self.Clone();
        g.Angle = double.IsFinite(self.Angle) ? self.Angle : 135;
        g.Stops = [.. self.Stops.Where(s => HexColor.Components(s.Color) is not null).Take(MaxStops)
            .Select(s => new GradientStop(s.Color, Clamp(s.Location, (0, 1), 0)))];
        var ids = new HashSet<string>();
        g.Sources = [.. self.Sources.Where(s => HexColor.Components(s.Color) is not null).Take(MaxSources).Select(s =>
        {
            var id = s.Id.Length == 0 || ids.Contains(s.Id) ? GradientSource.NewId() : s.Id;
            ids.Add(id);
            return s with { Id = id, X = Clamp(s.X, (0, 1), 0.5), Y = Clamp(s.Y, (0, 1), 0.5), Spread = Clamp(s.Spread, SpreadRange, 1) };
        })];
        g.Sharpness = Clamp(self.Sharpness, SharpnessRange, 2.2);
        g.CenterX = Clamp(self.CenterX, (0, 1), 0.5);
        g.CenterY = Clamp(self.CenterY, (0, 1), 0.5);
        g.Noise = Clamp(self.Noise, (0, 1), 0);
        g.Opacity = Clamp(self.Opacity, OpacityRange, 1);
        return g;
    }

    /// <summary>The colours that make up the gradient (for luminance and thumbnails).</summary>
    public static IReadOnlyList<string> Colors(this GradientSpec g) =>
        g.GradientKind == GradientSpec.Kind.Mesh ? [.. g.MeshSources().Select(s => s.Color)] : [.. g.NormalizedStops.Select(s => s.Color)];

    /// <summary>The mesh sources with valid colours, or two neutral ones when there are none.</summary>
    public static IReadOnlyList<GradientSource> MeshSources(this GradientSpec g)
    {
        var valid = g.Sources.Where(s => ColorValue.FromHex(s.Color) is not null).ToList();
        return valid.Count == 0 ? [new GradientSource("a", 0, 0, "#808080"), new GradientSource("b", 1, 1, "#B0B0B0")] : valid;
    }

    public static BackgroundSpec Clamped(this BackgroundSpec self)
    {
        var b = self.Clone();
        if (HexColor.Components(b.Color) is null) b.Color = "#F2F2F7";
        b.Gradient = self.Gradient.Clamped();
        return b;
    }

    /// <summary>Gradient Studio bounds applied by <c>MosaicLayout.Normalized()</c>.</summary>
    public static MosaicAppearance ClampedBackgrounds(this MosaicAppearance self)
    {
        var a = self.Clone();
        a.Background = self.Background.Clamped();
        a.NewTabBackground.Custom = self.NewTabBackground.Custom.Clamped();
        a.WorkspaceBackgrounds = self.WorkspaceBackgrounds
            .Where(p => p.Key.Length > 0 && p.Key.Length <= 64)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Take(64)
            .ToDictionary(p => p.Key, p => p.Value.Clamped());
        return a;
    }

    /// <summary>The window background for a workspace (its own, or the shared one).</summary>
    public static BackgroundSpec BackgroundForWorkspace(this MosaicAppearance a, string? id) =>
        id is not null && a.WorkspaceBackgrounds.TryGetValue(id, out var spec) ? spec : a.Background;

    // MARK: Mesh

    private static double Power(GradientSpec g) => Math.Min(Math.Max(double.IsFinite(g.Sharpness) ? g.Sharpness : 2.2, 0.5), 8);

    /// <summary>The mesh colour at a point (0…1); <paramref name="aspect"/> is width ÷ height.</summary>
    public static ColorValue MeshColor(this GradientSpec g, double x, double y, double aspect)
    {
        double l = 0, a = 0, b = 0, total = 0;
        var power = Power(g);
        foreach (var source in g.MeshSources())
        {
            var d = double.Hypot((x - source.X) * aspect, y - source.Y) / Math.Max(source.Spread, 0.05);
            var weight = 1 / Math.Pow(d + 0.0005, power);
            var lab = source.ColorValue.Oklab;
            l += weight * lab.L;
            a += weight * lab.A;
            b += weight * lab.B;
            total += weight;
        }
        return ColorValue.FromOklab(l / total, a / total, b / total);
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
        if (stops.Count == 0) return ColorValue.Fallback;
        var first = stops[0];
        var last = stops[^1];
        if (t <= first.Location) return ColorValue.FromHex(first.Color) ?? ColorValue.Fallback;
        if (t >= last.Location) return ColorValue.FromHex(last.Color) ?? ColorValue.Fallback;
        for (var i = 0; i + 1 < stops.Count; i++)
        {
            var (sa, sb) = (stops[i], stops[i + 1]);
            if (t < sa.Location || t > sb.Location) continue;
            var ca = ColorValue.FromHex(sa.Color) ?? ColorValue.Fallback;
            var cb = ColorValue.FromHex(sb.Color) ?? ColorValue.Fallback;
            var span = sb.Location - sa.Location;
            var f = span > 0 ? (t - sa.Location) / span : 0;
            return new ColorValue(ca.R + (cb.R - ca.R) * f, ca.G + (cb.G - ca.G) * f, ca.B + (cb.B - ca.B) * f);
        }
        return ColorValue.FromHex(last.Color) ?? ColorValue.Fallback;
    }

    /// <summary>The overall colour, averaged in OKLab.</summary>
    public static ColorValue AverageColor(this GradientSpec g)
    {
        var values = g.Colors().Select(ColorValue.FromHex).OfType<ColorValue>().ToList();
        if (values.Count == 0) return ColorValue.Fallback;
        double l = 0, a = 0, b = 0;
        foreach (var v in values)
        {
            var lab = v.Oklab;
            l += lab.L;
            a += lab.A;
            b += lab.B;
        }
        return ColorValue.FromOklab(l / values.Count, a / values.Count, b / values.Count);
    }

    /// <summary>Average relative luminance of colours, as the shell's background adaptation computes it.</summary>
    public static double Luminance(IEnumerable<string> colors)
    {
        var values = colors.Select(HexColor.Components).Where(c => c is not null)
            .Select(c => 0.2126 * c!.Value.Red + 0.7152 * c.Value.Green + 0.0722 * c.Value.Blue).ToList();
        return values.Count == 0 ? 0.5 : values.Average();
    }

    /// <summary>
    /// The same colours as another kind (switching kinds keeps the look): stops become sources
    /// laid along the angle; sources become stops by where they fall along it.
    /// </summary>
    public static GradientSpec Converted(this GradientSpec self, GradientSpec.Kind newKind)
    {
        var g = self.Clone();
        if (newKind == self.GradientKind) return g;
        g.GradientKind = newKind;
        var radians = self.Angle * Math.PI / 180;
        double dx = Math.Cos(radians), dy = Math.Sin(radians);
        if (newKind == GradientSpec.Kind.Mesh && self.GradientKind != GradientSpec.Kind.Mesh)
        {
            g.Sources = [.. self.NormalizedStops.Select((stop, index) =>
            {
                var offset = stop.Location - 0.5;
                return new GradientSource($"s{index}-{GradientSource.NewId()[..8]}",
                    Math.Min(Math.Max(0.5 + dx * offset, 0), 1), Math.Min(Math.Max(0.5 + dy * offset, 0), 1), stop.Color);
            })];
        }
        else if (self.GradientKind == GradientSpec.Kind.Mesh && newKind != GradientSpec.Kind.Mesh)
        {
            var projected = self.MeshSources().Select(s => (P: (s.X - 0.5) * dx + (s.Y - 0.5) * dy, s.Color))
                .OrderBy(p => p.P).ToList();
            var lo = projected.Count > 0 ? projected[0].P : 0;
            var hi = projected.Count > 0 ? projected[^1].P : 1;
            var span = hi - lo;
            g.Stops = [.. projected.Select((item, index) => new GradientStop(item.Color,
                span > 0.0001 ? (item.P - lo) / span : (double)index / Math.Max(projected.Count - 1, 1)))];
        }
        return g;
    }

    /// <summary>The gradient without its finish (grain, strength, Raw): what a preset match compares.</summary>
    public static bool SameDesign(this GradientSpec a, GradientSpec b)
    {
        var x = a.Clone();
        var y = b.Clone();
        x.Noise = y.Noise = 0;
        x.Opacity = y.Opacity = 1;
        x.Raw = y.Raw = false;
        return x == y;
    }
}

/// <summary>Ready-made gradients: Eminence's free-form designs and the shell's soft linear backgrounds.</summary>
public static class GradientPresets
{
    private static (string Name, GradientSpec Gradient) Mesh(string name, (double X, double Y, string Color)[] points, double sharpness = 2.2)
    {
        var slug = name.ToLowerInvariant().Replace(" ", "-");
        return (name, new GradientSpec(GradientSpec.Kind.Mesh, 135, [],
            points.Select((p, i) => new GradientSource($"{slug}-{i}", p.X, p.Y, p.Color)), sharpness));
    }

    private static (string Name, GradientSpec Gradient) Linear(string name, double angle, (string Color, double Location)[] stops) =>
        (name, new GradientSpec(GradientSpec.Kind.Linear, angle, stops.Select(s => new GradientStop(s.Color, s.Location))));

    private static readonly (string Name, GradientSpec Gradient)[] mesh =
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

    private static readonly (string Name, GradientSpec Gradient)[] linear =
    [
        Linear("Lavender", 125, [("#C9BFF7", 0), ("#E7D6F5", 0.55), ("#BFD3F5", 1)]),
        Linear("Dusk", 135, [("#FFB88C", 0), ("#FF8FA3", 0.5), ("#C79BFF", 1)]),
        Linear("Sky", 140, [("#A1C4FD", 0), ("#C2E9FB", 1)]),
        Linear("Mint", 120, [("#B8F1D3", 0), ("#D9F6C4", 1)]),
        Linear("Night", 160, [("#1B1024", 0), ("#0F0E15", 0.6), ("#24101A", 1)]),
        Linear("Graphite", 90, [("#3A3D44", 0), ("#24262B", 1)]),
    ];

    /// <summary>Copies, so callers may change what they get.</summary>
    public static IReadOnlyList<(string Name, GradientSpec Gradient)> MeshPresets => [.. mesh.Select(p => (p.Name, p.Gradient.Clone()))];

    public static IReadOnlyList<(string Name, GradientSpec Gradient)> LinearPresets => [.. linear.Select(p => (p.Name, p.Gradient.Clone()))];

    public static IReadOnlyList<(string Name, GradientSpec Gradient)> All => [.. MeshPresets, .. LinearPresets];

    /// <summary>A preset by name, ignoring case and spaces.</summary>
    public static GradientSpec? Named(string name)
    {
        static string Key(string s) => s.ToLowerInvariant().Replace(" ", "");
        return All.FirstOrDefault(p => Key(p.Name) == Key(name)).Gradient;
    }
}
