using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Shell;

// Colours, gradients, backgrounds and the appearance of a Mosaic. Port of the appearance half
// of Sources/MosaicCore/Shell/MosaicLayout.swift plus the data types and bounds of
// Sources/MosaicCore/Canvas/Backgrounds.swift that the layout's JSON contains (the canvas area
// adds rendering, presets and editing on top). Decoding is forgiving exactly where the Swift
// decoders are: a missing or mistyped value takes its default.
// Owner: shell (data and bounds); canvas extends.

/// <summary>Colours are <c>#RRGGBB</c> / <c>#RRGGBBAA</c> strings, or <c>"system"</c> for the system accent.</summary>
public static class HexColor
{
    public const string System = "system";

    /// <summary>Red, green, blue and alpha in 0…1, or null if <paramref name="hex"/> is not a colour.</summary>
    public static (double Red, double Green, double Blue, double Alpha)? Components(string? hex)
    {
        if (hex is null) return null;
        var text = hex.Trim(' ', '\t');
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length is not (6 or 8)) return null;
        if (!ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return null;
        var hasAlpha = text.Length == 8;
        double r = ((value >> (hasAlpha ? 24 : 16)) & 0xFF) / 255.0;
        double g = ((value >> (hasAlpha ? 16 : 8)) & 0xFF) / 255.0;
        double b = ((value >> (hasAlpha ? 8 : 0)) & 0xFF) / 255.0;
        double a = hasAlpha ? (value & 0xFF) / 255.0 : 1;
        return (r, g, b, a);
    }

    public static bool IsValid(string? hex) => hex == System || Components(hex) is not null;

    /// <summary><c>#RRGGBB</c> (or <c>#RRGGBBAA</c> when not opaque) for components in 0…1.</summary>
    public static string String(double red, double green, double blue, double alpha = 1)
    {
        static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
        var text = $"#{Byte(red):X2}{Byte(green):X2}{Byte(blue):X2}";
        return alpha >= 0.999 ? text : text + $"{Byte(alpha):X2}";
    }

    /// <summary>Average relative luminance of the colours (0 black … 1 white), 0.5 when there are none.</summary>
    public static double Luminance(IEnumerable<string> colors)
    {
        var values = colors.Select(Components).Where(c => c is not null)
            .Select(c => 0.2126 * c!.Value.Red + 0.7152 * c.Value.Green + 0.0722 * c.Value.Blue).ToList();
        return values.Count == 0 ? 0.5 : values.Average();
    }
}

/// <summary>Reading helpers with Swift <c>try? decodeIfPresent</c> semantics.</summary>
internal static class SwiftDecode
{
    public static double Double(JsonObject? o, string key, double fallback) => TryDouble(o?[key]) ?? fallback;

    public static double? TryDouble(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out JsonElement e) && e.TryGetDouble(out var ed)) return ed;
        return null;
    }

    public static int Int(JsonObject? o, string key, int fallback)
    {
        if (o?[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return fallback;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out JsonElement e) && e.TryGetInt32(out var ei)) return ei;
        if (TryDouble(v) is { } d && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        return fallback;
    }

    public static bool Bool(JsonObject? o, string key, bool fallback)
    {
        if (o?[key] is not JsonValue v) return fallback;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static string? TryString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static string String(JsonObject? o, string key, string fallback) => TryString(o?[key]) ?? fallback;

    public static T Enum<T>(JsonObject? o, string key, T fallback) where T : struct, System.Enum =>
        JsonEnum.Parse<T>(TryString(o?[key])) ?? fallback;
}

public sealed class GradientStop
{
    public string Color { get; set; }
    /// <summary>0…1 along the gradient.</summary>
    public double Location { get; set; }

    public GradientStop(string color, double location)
    {
        Color = color;
        Location = location;
    }

    public GradientStop Clone() => new(Color, Location);

    internal JsonObject ToJson() => new() { ["color"] = Color, ["location"] = Location };

    /// <summary>Strict, like Swift's synthesised decoder: both keys must be there with the right types.</summary>
    internal static GradientStop? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        if (SwiftDecode.TryString(o["color"]) is not { } color || SwiftDecode.TryDouble(o["location"]) is not { } location) return null;
        return new GradientStop(color, location);
    }
}

/// <summary>One colour source of a mesh gradient (Gradient Studio).</summary>
public sealed class GradientSource
{
    public string Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string Color { get; set; }
    public double Spread { get; set; }

    public GradientSource(string? id, double x, double y, string color, double spread = 1)
    {
        Id = id ?? Guid.NewGuid().ToString().ToUpperInvariant();
        X = x;
        Y = y;
        Color = color;
        Spread = spread;
    }

    public GradientSource Clone() => new(Id, X, Y, Color, Spread);

    internal JsonObject ToJson() => new() { ["id"] = Id, ["x"] = X, ["y"] = Y, ["color"] = Color, ["spread"] = Spread };

    /// <summary>Forgiving; Eminence's <c>hex</c> key is accepted for <c>color</c>.</summary>
    internal static GradientSource? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new GradientSource(
            SwiftDecode.TryString(o["id"]),
            SwiftDecode.Double(o, "x", 0.5),
            SwiftDecode.Double(o, "y", 0.5),
            SwiftDecode.TryString(o["color"]) ?? SwiftDecode.TryString(o["hex"]) ?? "#808080",
            SwiftDecode.Double(o, "spread", 1));
    }
}

/// <summary>A gradient description (linear, radial, angular, or the Gradient Studio's mesh).</summary>
public sealed class GradientSpec
{
    public const int MaxStops = 16;
    public const int MaxSources = 32;

    public GradientKind Kind { get; set; } = GradientKind.Linear;
    /// <summary>Degrees; 0 runs left to right, 90 top to bottom.</summary>
    public double Angle { get; set; } = 135;
    public List<GradientStop> Stops { get; set; } = [];
    /// <summary>Colour sources of a mesh gradient.</summary>
    public List<GradientSource> Sources { get; set; } = [];
    /// <summary>How sharply mesh colours meet (1…6).</summary>
    public double Sharpness { get; set; } = 2.2;
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    /// <summary>Film grain, 0…1.</summary>
    public double Noise { get; set; }
    /// <summary>How strongly the gradient covers the system window colour, 0.1…1.</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>Shown exactly as chosen in light and dark mode (Eminence's "Raw").</summary>
    public bool Raw { get; set; }

    public GradientSpec() { }

    public GradientSpec(GradientKind kind, double angle, params GradientStop[] stops)
    {
        Kind = kind;
        Angle = angle;
        Stops = [.. stops];
    }

    public GradientSpec Clone() => new()
    {
        Kind = Kind, Angle = Angle, Stops = [.. Stops.Select(s => s.Clone())], Sources = [.. Sources.Select(s => s.Clone())],
        Sharpness = Sharpness, CenterX = CenterX, CenterY = CenterY, Noise = Noise, Opacity = Opacity, Raw = Raw,
    };

    /// <summary>Stops sorted by location, clamped to 0…1, invalid colours removed; at least two.</summary>
    public List<GradientStop> NormalizedStops
    {
        get
        {
            var result = Stops.Where(s => HexColor.Components(s.Color) is not null)
                .Select(s => new GradientStop(s.Color, Math.Clamp(s.Location, 0, 1)))
                .OrderBy(s => s.Location).ToList();
            if (result.Count == 0) result.Add(new GradientStop("#808080", 0));
            if (result.Count == 1) result.Add(new GradientStop(result[0].Color, 1));
            return result;
        }
    }

    /// <summary>The mesh sources with valid colours, or two neutral ones when there are none.</summary>
    public List<GradientSource> MeshSources
    {
        get
        {
            var valid = Sources.Where(s => HexColor.Components(s.Color) is not null).ToList();
            return valid.Count > 0 ? valid :
                [new GradientSource("a", 0, 0, "#808080"), new GradientSource("b", 1, 1, "#B0B0B0")];
        }
    }

    /// <summary>The colours that make up the gradient (for luminance and thumbnails).</summary>
    public List<string> Colors => Kind == GradientKind.Mesh
        ? [.. MeshSources.Select(s => s.Color)]
        : [.. NormalizedStops.Select(s => s.Color)];

    /// <summary>Every number in range, invalid colours dropped, lists capped (Backgrounds.swift).</summary>
    public GradientSpec Clamped()
    {
        static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
        var g = Clone();
        g.Angle = double.IsFinite(Angle) ? Angle : 135;
        g.Stops = [.. Stops.Where(s => HexColor.Components(s.Color) is not null).Take(MaxStops)
            .Select(s => new GradientStop(s.Color, Clamp(s.Location, 0, 1, 0)))];
        var ids = new HashSet<string>();
        g.Sources = [.. Sources.Where(s => HexColor.Components(s.Color) is not null).Take(MaxSources).Select(s =>
        {
            var c = s.Clone();
            c.X = Clamp(c.X, 0, 1, 0.5);
            c.Y = Clamp(c.Y, 0, 1, 0.5);
            c.Spread = Clamp(c.Spread, 0.3, 3.0, 1);
            if (c.Id.Length == 0 || ids.Contains(c.Id)) c.Id = Guid.NewGuid().ToString().ToUpperInvariant();
            ids.Add(c.Id);
            return c;
        })];
        g.Sharpness = Clamp(Sharpness, 1, 6, 2.2);
        g.CenterX = Clamp(CenterX, 0, 1, 0.5);
        g.CenterY = Clamp(CenterY, 0, 1, 0.5);
        g.Noise = Clamp(Noise, 0, 1, 0);
        g.Opacity = Clamp(Opacity, 0.1, 1, 1);
        return g;
    }

    internal JsonObject ToJson() => new()
    {
        ["kind"] = Kind.Raw(),
        ["angle"] = Angle,
        ["stops"] = new JsonArray([.. Stops.Select(s => (JsonNode)s.ToJson())]),
        ["sources"] = new JsonArray([.. Sources.Select(s => (JsonNode)s.ToJson())]),
        ["sharpness"] = Sharpness,
        ["centerX"] = CenterX,
        ["centerY"] = CenterY,
        ["noise"] = Noise,
        ["opacity"] = Opacity,
        ["raw"] = Raw,
    };

    internal static GradientSpec? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new GradientSpec
        {
            Kind = SwiftDecode.Enum(o, "kind", GradientKind.Linear),
            Angle = SwiftDecode.Double(o, "angle", 135),
            Stops = DecodeAll(o["stops"], GradientStop.FromJson),
            Sources = DecodeAll(o["sources"], GradientSource.FromJson),
            Sharpness = SwiftDecode.Double(o, "sharpness", 2.2),
            CenterX = SwiftDecode.Double(o, "centerX", 0.5),
            CenterY = SwiftDecode.Double(o, "centerY", 0.5),
            Noise = SwiftDecode.Double(o, "noise", 0),
            Opacity = SwiftDecode.Double(o, "opacity", 1),
            Raw = SwiftDecode.Bool(o, "raw", false),
        };
    }

    /// <summary>An array where every element must decode, or nothing (Swift's array decoding inside <c>try?</c>).</summary>
    internal static List<T> DecodeAll<T>(JsonNode? node, Func<JsonNode?, T?> decode) where T : class
    {
        if (node is not JsonArray array) return [];
        var result = new List<T>(array.Count);
        foreach (var item in array)
        {
            if (decode(item) is not { } value) return [];
            result.Add(value);
        }
        return result;
    }
}

public sealed class BackgroundSpec
{
    public const string DefaultColor = "#F2F2F7";

    public BackgroundKind Kind { get; set; } = BackgroundKind.System;
    public string Color { get; set; } = DefaultColor;
    public GradientSpec Gradient { get; set; } = new();

    public BackgroundSpec() { }

    public BackgroundSpec(BackgroundKind kind, string color = DefaultColor, GradientSpec? gradient = null)
    {
        Kind = kind;
        Color = color;
        Gradient = gradient ?? new GradientSpec();
    }

    public BackgroundSpec Clone() => new(Kind, Color, Gradient.Clone());

    public BackgroundSpec Clamped()
    {
        var b = Clone();
        if (HexColor.Components(b.Color) is null) b.Color = DefaultColor;
        b.Gradient = Gradient.Clamped();
        return b;
    }

    internal JsonObject ToJson() => new() { ["kind"] = Kind.Raw(), ["color"] = Color, ["gradient"] = Gradient.ToJson() };

    internal static BackgroundSpec? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new BackgroundSpec(
            SwiftDecode.Enum(o, "kind", BackgroundKind.System),
            SwiftDecode.String(o, "color", DefaultColor),
            GradientSpec.FromJson(o["gradient"]) ?? new GradientSpec());
    }
}

/// <summary>The New Tab page's background.</summary>
public sealed class NewTabBackground
{
    public NewTabBackgroundMode Mode { get; set; } = NewTabBackgroundMode.Plain;
    public BackgroundSpec Custom { get; set; } = DefaultCustom();

    /// <summary>The Gradient Studio's first preset ("Sunset", a mesh), as on macOS.</summary>
    public static BackgroundSpec DefaultCustom() => new(BackgroundKind.Gradient, BackgroundSpec.DefaultColor, new GradientSpec
    {
        Kind = GradientKind.Mesh,
        Angle = 135,
        Sources =
        [
            new GradientSource("sunset-0", 0.05, 0.1, "#FF7E5F"),
            new GradientSource("sunset-1", 0.95, 0.15, "#FEB47B"),
            new GradientSource("sunset-2", 0.5, 0.95, "#6A3093"),
        ],
        Sharpness = 2.2,
    });

    public NewTabBackground Clone() => new() { Mode = Mode, Custom = Custom.Clone() };

    internal JsonObject ToJson() => new() { ["mode"] = Mode.Raw(), ["custom"] = Custom.ToJson() };

    internal static NewTabBackground? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new NewTabBackground
        {
            Mode = SwiftDecode.Enum(o, "mode", NewTabBackgroundMode.Plain),
            Custom = BackgroundSpec.FromJson(o["custom"]) ?? DefaultCustom(),
        };
    }
}

public sealed class MosaicAppearance
{
    public ShellDensity Density { get; set; } = ShellDensity.Regular;
    /// <summary>Corner radius of controls (address field, tabs, buttons), in points (DIPs).</summary>
    public double CornerRadius { get; set; } = 8;
    /// <summary>Gap between the page and the window chrome (Arc's floating card); 0 = flush.</summary>
    public double ContentInset { get; set; }
    /// <summary>Corner radius of the page card.</summary>
    public double ContentCornerRadius { get; set; }
    public string Accent { get; set; } = HexColor.System;
    public ColorSchemePreference ColorScheme { get; set; } = ColorSchemePreference.System;
    public ChromeMaterial Material { get; set; } = ChromeMaterial.LiquidGlass;
    public BackgroundSpec Background { get; set; } = new();
    /// <summary>The toolbar row has its own lighter surface that the selected tab merges into (Chrome).</summary>
    public bool ToolbarSurface { get; set; }
    /// <summary>A soft accent-coloured glow around the page (Opera GX).</summary>
    public bool Glow { get; set; }
    /// <summary>The New Tab page's background (canvas).</summary>
    public NewTabBackground NewTabBackground { get; set; } = new();
    /// <summary>Window backgrounds for particular workspaces, keyed by workspace ID (canvas).</summary>
    public Dictionary<string, BackgroundSpec> WorkspaceBackgrounds { get; set; } = new(StringComparer.Ordinal);

    public MosaicAppearance Clone() => new()
    {
        Density = Density, CornerRadius = CornerRadius, ContentInset = ContentInset, ContentCornerRadius = ContentCornerRadius,
        Accent = Accent, ColorScheme = ColorScheme, Material = Material, Background = Background.Clone(),
        ToolbarSurface = ToolbarSurface, Glow = Glow, NewTabBackground = NewTabBackground.Clone(),
        WorkspaceBackgrounds = WorkspaceBackgrounds.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
    };

    /// <summary>The window background for a workspace (its own, or the shared one).</summary>
    public BackgroundSpec BackgroundForWorkspace(string? id) =>
        id is not null && WorkspaceBackgrounds.TryGetValue(id, out var spec) ? spec : Background;

    /// <summary>Gradient Studio bounds applied by <see cref="MosaicLayout.Normalized"/>.</summary>
    public MosaicAppearance ClampedBackgrounds()
    {
        var a = Clone();
        a.Background = Background.Clamped();
        a.NewTabBackground.Custom = NewTabBackground.Custom.Clamped();
        a.WorkspaceBackgrounds = WorkspaceBackgrounds
            .Where(p => p.Key.Length > 0 && p.Key.Length <= 64)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Take(64)
            .ToDictionary(p => p.Key, p => p.Value.Clamped(), StringComparer.Ordinal);
        return a;
    }

    internal JsonObject ToJson()
    {
        var workspaces = new JsonObject();
        foreach (var (key, value) in WorkspaceBackgrounds) workspaces[key] = value.ToJson();
        return new JsonObject
        {
            ["density"] = Density.Raw(),
            ["cornerRadius"] = CornerRadius,
            ["contentInset"] = ContentInset,
            ["contentCornerRadius"] = ContentCornerRadius,
            ["accent"] = Accent,
            ["colorScheme"] = ColorScheme.Raw(),
            ["material"] = Material.Raw(),
            ["background"] = Background.ToJson(),
            ["toolbarSurface"] = ToolbarSurface,
            ["glow"] = Glow,
            ["newTabBackground"] = NewTabBackground.ToJson(),
            ["workspaceBackgrounds"] = workspaces,
        };
    }

    internal static MosaicAppearance? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var d = new MosaicAppearance();
        return new MosaicAppearance
        {
            Density = SwiftDecode.Enum(o, "density", d.Density),
            CornerRadius = SwiftDecode.Double(o, "cornerRadius", d.CornerRadius),
            ContentInset = SwiftDecode.Double(o, "contentInset", d.ContentInset),
            ContentCornerRadius = SwiftDecode.Double(o, "contentCornerRadius", d.ContentCornerRadius),
            Accent = SwiftDecode.String(o, "accent", d.Accent),
            ColorScheme = SwiftDecode.Enum(o, "colorScheme", d.ColorScheme),
            Material = SwiftDecode.Enum(o, "material", d.Material),
            Background = BackgroundSpec.FromJson(o["background"]) ?? d.Background,
            ToolbarSurface = SwiftDecode.Bool(o, "toolbarSurface", d.ToolbarSurface),
            Glow = SwiftDecode.Bool(o, "glow", d.Glow),
            NewTabBackground = NewTabBackground.FromJson(o["newTabBackground"]) ?? d.NewTabBackground,
            WorkspaceBackgrounds = DecodeWorkspaces(o["workspaceBackgrounds"]) ?? d.WorkspaceBackgrounds,
        };
    }

    private static Dictionary<string, BackgroundSpec>? DecodeWorkspaces(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var result = new Dictionary<string, BackgroundSpec>(StringComparer.Ordinal);
        foreach (var (key, value) in o)
        {
            if (BackgroundSpec.FromJson(value) is not { } spec) return null;
            result[key] = spec;
        }
        return result;
    }
}
