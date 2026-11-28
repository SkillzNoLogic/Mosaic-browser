using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

// INTERIM (see MosaicLayout.cs): JSON for the shell's layout types, matching what the macOS
// app's Codable implementation reads and writes. Decoding mirrors Swift's
// `(try? c.decodeIfPresent(T.self, forKey:)) ?? fallback`: a missing, null or wrongly typed
// value takes the fallback; a list or dictionary with one undecodable element falls back as a
// whole (that is what Swift's synthesized decoding does); keys are written sorted.

namespace Mosaic.Core.Canvas.Interim;

public static class LayoutJson
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(JsonNode node, bool pretty = true) => node.ToJsonString(pretty ? Pretty : Compact);

    // MARK: Readers (null = "use the fallback")

    internal static double? Double(JsonObject o, string key) => o[key] is JsonValue v ? Number(v) : null;

    /// <summary>A JSON number as a double, however the node holds it (parsed text, or an int/double set in code).</summary>
    public static double? Number(JsonValue v)
    {
        if (v.GetValueKind() != JsonValueKind.Number) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out d) ? d : null;
    }

    internal static int? Int(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v || Number(v) is not { } d) return null;
        return d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;
    }

    internal static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v
        ? v.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;

    internal static string? String(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    internal static T? Enum<T>(JsonObject o, string key) where T : struct, System.Enum => RawValues.Parse<T>(String(o, key));

    /// <summary>A list whose every element decodes, or null (Swift fails the whole array).</summary>
    internal static List<T>? List<T>(JsonObject o, string key, Func<JsonNode?, T?> element) where T : class
    {
        if (o[key] is not JsonArray array) return null;
        var result = new List<T>(array.Count);
        foreach (var item in array)
        {
            if (element(item) is not { } value) return null;
            result.Add(value);
        }
        return result;
    }

    internal static JsonNode Number(double value) => JsonValue.Create(double.IsFinite(value) ? value : 0)!;

    // MARK: GradientStop / GradientSpec / BackgroundSpec

    public static JsonObject Encode(GradientStop stop) => new() { ["color"] = stop.Color, ["location"] = Number(stop.Location) };

    public static GradientStop? DecodeStop(JsonNode? node) =>
        node is JsonObject o && String(o, "color") is { } color && Double(o, "location") is { } location
            ? new GradientStop(color, location) : null;

    public static JsonObject Encode(GradientSpec g) => new()
    {
        ["angle"] = Number(g.Angle),
        ["centerX"] = Number(g.CenterX),
        ["centerY"] = Number(g.CenterY),
        ["kind"] = g.GradientKind.Raw(),
        ["noise"] = Number(g.Noise),
        ["opacity"] = Number(g.Opacity),
        ["raw"] = g.Raw,
        ["sharpness"] = Number(g.Sharpness),
        ["sources"] = new JsonArray([.. g.Sources.Select(s => (JsonNode)CanvasJson.Encode(s))]),
        ["stops"] = new JsonArray([.. g.Stops.Select(s => (JsonNode)Encode(s))]),
    };

    public static GradientSpec? DecodeGradient(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new GradientSpec
        {
            GradientKind = Enum<GradientSpec.Kind>(o, "kind") ?? GradientSpec.Kind.Linear,
            Angle = Double(o, "angle") ?? 135,
            Stops = List(o, "stops", DecodeStop) ?? [],
            Sources = List(o, "sources", CanvasJson.DecodeSource) ?? [],
            Sharpness = Double(o, "sharpness") ?? 2.2,
            CenterX = Double(o, "centerX") ?? 0.5,
            CenterY = Double(o, "centerY") ?? 0.5,
            Noise = Double(o, "noise") ?? 0,
            Opacity = Double(o, "opacity") ?? 1,
            Raw = Bool(o, "raw") ?? false,
        };
    }

    public static JsonObject Encode(BackgroundSpec b) => new()
    {
        ["color"] = b.Color,
        ["gradient"] = Encode(b.Gradient),
        ["kind"] = b.BackgroundKind.Raw(),
    };

    public static BackgroundSpec? DecodeBackground(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new BackgroundSpec
        {
            BackgroundKind = Enum<BackgroundSpec.Kind>(o, "kind") ?? BackgroundSpec.Kind.System,
            Color = String(o, "color") ?? "#F2F2F7",
            Gradient = DecodeGradient(o["gradient"]) ?? new GradientSpec(),
        };
    }

    // MARK: Appearance

    public static JsonObject Encode(MosaicAppearance a)
    {
        var workspaces = new JsonObject();
        foreach (var (key, value) in a.WorkspaceBackgrounds.OrderBy(p => p.Key, StringComparer.Ordinal)) workspaces[key] = Encode(value);
        return new JsonObject
        {
            ["accent"] = a.Accent,
            ["background"] = Encode(a.Background),
            ["colorScheme"] = a.ColorScheme.Raw(),
            ["contentCornerRadius"] = Number(a.ContentCornerRadius),
            ["contentInset"] = Number(a.ContentInset),
            ["cornerRadius"] = Number(a.CornerRadius),
            ["density"] = a.Density.Raw(),
            ["glow"] = a.Glow,
            ["material"] = a.Material.Raw(),
            ["newTabBackground"] = CanvasJson.Encode(a.NewTabBackground),
            ["toolbarSurface"] = a.ToolbarSurface,
            ["workspaceBackgrounds"] = workspaces,
        };
    }

    /// <summary>Missing fields take MosaicAppearance's own defaults (not the preset's), as on macOS.</summary>
    public static MosaicAppearance? DecodeAppearance(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var d = new MosaicAppearance();
        Dictionary<string, BackgroundSpec>? workspaces = null;
        if (o["workspaceBackgrounds"] is JsonObject dict)
        {
            workspaces = [];
            foreach (var (key, value) in dict)
            {
                if (DecodeBackground(value) is not { } spec) { workspaces = null; break; }
                workspaces[key] = spec;
            }
        }
        return new MosaicAppearance
        {
            Density = Enum<ShellDensity>(o, "density") ?? d.Density,
            CornerRadius = Double(o, "cornerRadius") ?? d.CornerRadius,
            ContentInset = Double(o, "contentInset") ?? d.ContentInset,
            ContentCornerRadius = Double(o, "contentCornerRadius") ?? d.ContentCornerRadius,
            Accent = String(o, "accent") ?? d.Accent,
            ColorScheme = Enum<ColorSchemePreference>(o, "colorScheme") ?? d.ColorScheme,
            Material = Enum<ChromeMaterial>(o, "material") ?? d.Material,
            Background = DecodeBackground(o["background"]) ?? d.Background.Clone(),
            ToolbarSurface = Bool(o, "toolbarSurface") ?? d.ToolbarSurface,
            Glow = Bool(o, "glow") ?? d.Glow,
            NewTabBackground = CanvasJson.DecodeNewTab(o["newTabBackground"]) ?? d.NewTabBackground.Clone(),
            WorkspaceBackgrounds = workspaces ?? d.WorkspaceBackgrounds.ToDictionary(p => p.Key, p => p.Value.Clone()),
        };
    }

    // MARK: Layout

    public static JsonObject Encode(MosaicLayout layout)
    {
        var regions = new JsonObject();
        foreach (var region in RawValues.All<ShellRegion>().OrderBy(r => r.Raw(), StringComparer.Ordinal))
        {
            if (layout.Regions.TryGetValue(region, out var list) && list.Count > 0)
                regions[region.Raw()] = new JsonArray([.. list.Select(c => (JsonNode)JsonValue.Create(c.Raw())!)]);
        }
        return new JsonObject
        {
            ["addressBar"] = layout.AddressBar.Raw(),
            ["appearance"] = Encode(layout.Appearance),
            ["bookmarksBar"] = layout.BookmarksBar,
            ["name"] = layout.Name,
            ["preset"] = layout.Preset.Raw(),
            ["regions"] = regions,
            ["schemaVersion"] = layout.SchemaVersion,
            ["sidebar"] = layout.Sidebar.Raw(),
            ["sidebarWidth"] = Number(layout.SidebarWidth),
            ["tabPlacement"] = layout.TabPlacement.Raw(),
            ["tabStyle"] = layout.TabStyle.Raw(),
        };
    }

    /// <summary>The layout in an object, or null when it is not an object (Swift's decode throws).</summary>
    public static MosaicLayout? TryDecodeLayout(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var preset = Enum<MosaicPresetID>(o, "preset") ?? MosaicPresetID.Chrome;
        var d = MosaicLayout.PresetLayout(preset);
        var layout = new MosaicLayout
        {
            SchemaVersion = Int(o, "schemaVersion") ?? MosaicLayout.CurrentSchemaVersion,
            Preset = preset,
            Name = String(o, "name") ?? d.Name,
            TabPlacement = Enum<TabPlacement>(o, "tabPlacement") ?? d.TabPlacement,
            TabStyle = Enum<TabStyle>(o, "tabStyle") ?? d.TabStyle,
            AddressBar = Enum<AddressBarStyle>(o, "addressBar") ?? d.AddressBar,
            BookmarksBar = Bool(o, "bookmarksBar") ?? d.BookmarksBar,
            Sidebar = Enum<SidebarBehavior>(o, "sidebar") ?? d.Sidebar,
            SidebarWidth = Double(o, "sidebarWidth") ?? d.SidebarWidth,
            Appearance = DecodeAppearance(o["appearance"]) ?? d.Appearance,
            Regions = d.Regions,
        };
        // Regions are read as plain strings so unknown names are skipped; anything that is not
        // a dictionary of string lists keeps the preset's arrangement.
        if (o["regions"] is JsonObject raw && raw.All(p => p.Value is JsonArray a && a.All(i => i is JsonValue v && v.GetValueKind() == JsonValueKind.String)))
        {
            var regions = new Dictionary<ShellRegion, List<ShellComponent>>();
            foreach (var (key, value) in raw)
            {
                if (RawValues.Parse<ShellRegion>(key) is not { } region) continue;
                regions[region] = [.. value!.AsArray().Select(i => RawValues.Parse<ShellComponent>(i!.GetValue<string>())).OfType<ShellComponent>()];
            }
            layout.Regions = regions;
        }
        return layout;
    }

    public static MosaicLayout DecodeLayout(JsonNode? node) =>
        TryDecodeLayout(node) ?? throw new JsonException("The JSON is not a Mosaic layout object.");
}
