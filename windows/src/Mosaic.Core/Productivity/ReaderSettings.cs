using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Productivity;

public enum ReaderFont { Sans, Serif }

public enum ReaderTheme { Light, Sepia, Dark }

public static class ReaderSettingNames
{
    /// <summary>Stored and Gecko-preference value: "sans-serif" / "serif".</summary>
    public static string Id(this ReaderFont font) => font == ReaderFont.Serif ? "serif" : "sans-serif";

    public static string Title(this ReaderFont font) => font == ReaderFont.Serif ? "Serif" : "Sans-serif";

    public static string Id(this ReaderTheme theme) => theme switch
    {
        ReaderTheme.Sepia => "sepia",
        ReaderTheme.Dark => "dark",
        _ => "light",
    };

    public static string Title(this ReaderTheme theme) => theme.ToString();

    public static ReaderFont? FontFromId(string? id) => id switch
    {
        "sans-serif" => ReaderFont.Sans,
        "serif" => ReaderFont.Serif,
        _ => null,
    };

    public static ReaderTheme? ThemeFromId(string? id) => id switch
    {
        "light" => ReaderTheme.Light,
        "sepia" => ReaderTheme.Sepia,
        "dark" => ReaderTheme.Dark,
        _ => null,
    };
}

/// <summary>
/// Reader view typography and colours, shared by both engines. Port of
/// <c>Sources/MosaicCore/Productivity/ReaderSettings.swift</c>.
/// </summary>
/// <remarks>
/// The scales are Firefox's own (<c>reader.*</c> preferences), so Gecko's reader view and
/// Mosaic's Chromium reader show the same text for the same settings: font size step 1…9 →
/// 12…28 px, content width step 1…9 → 20…60 em, line spacing step 1…9 → 1.0…2.6 em. Steps are
/// clamped on assignment. Stored as macOS stores it:
/// <c>{"font":"serif","fontSize":5,"contentWidth":3,"lineHeight":4,"theme":"light"}</c>.
/// </remarks>
public sealed record ReaderSettings
{
    public const int MinStep = 1;
    public const int MaxStep = 9;

    private readonly int fontSize = 5;
    private readonly int contentWidth = 3;
    private readonly int lineHeight = 4;

    /// <summary>Firefox's defaults, except the theme (Firefox follows the system; Mosaic starts light).</summary>
    public ReaderSettings() { }

    public ReaderFont Font { get; init; } = ReaderFont.Sans;
    public int FontSize { get => fontSize; init => fontSize = Clamp(value); }
    public int ContentWidth { get => contentWidth; init => contentWidth = Clamp(value); }
    public int LineHeight { get => lineHeight; init => lineHeight = Clamp(value); }
    public ReaderTheme Theme { get; init; } = ReaderTheme.Light;

    internal static int Clamp(int value) => Math.Clamp(value, MinStep, MaxStep);

    public int FontSizePixels => 10 + 2 * FontSize;
    public double ContentWidthEm => 20 + 5 * (ContentWidth - 1);
    public double LineHeightEm => 1 + 0.2 * (LineHeight - 1);

    // ---- Gecko preferences ----

    /// <summary>The Gecko preferences that give its reader view these settings.</summary>
    public JsonObject GeckoPreferences() => new()
    {
        ["reader.font_type"] = Font.Id(),
        ["reader.font_size"] = FontSize,
        ["reader.content_width"] = ContentWidth,
        ["reader.line_height"] = LineHeight,
        ["reader.color_scheme"] = Theme.Id(),
    };

    /// <summary>
    /// Takes over what the user chose in Gecko's own reader toolbar. Values Mosaic does not offer
    /// (Gecko's "auto"/"contrast"/"gray" themes, monospace, its extra-large font steps) keep the
    /// current setting or are clamped.
    /// </summary>
    public ReaderSettings MergingGeckoPreferences(JsonObject? prefs)
    {
        if (prefs is null) return this;
        var result = this;
        if (ReaderSettingNames.FontFromId(Text(prefs["reader.font_type"])) is { } font) result = result with { Font = font };
        if (Number(prefs["reader.font_size"]) is { } size) result = result with { FontSize = size };
        if (Number(prefs["reader.content_width"]) is { } width) result = result with { ContentWidth = width };
        if (Number(prefs["reader.line_height"]) is { } height) result = result with { LineHeight = height };
        if (ReaderSettingNames.ThemeFromId(Text(prefs["reader.color_scheme"])) is { } theme) result = result with { Theme = theme };
        return result;
    }

    // ---- Stored form ----

    public JsonObject ToJsonObject() => new()
    {
        ["font"] = Font.Id(),
        ["fontSize"] = FontSize,
        ["contentWidth"] = ContentWidth,
        ["lineHeight"] = LineHeight,
        ["theme"] = Theme.Id(),
    };

    public string ToJson() => ToJsonObject().ToJsonString();

    /// <summary>Reads the stored form leniently: missing or invalid values take their defaults.</summary>
    public static ReaderSettings FromJsonObject(JsonObject? json)
    {
        var defaults = new ReaderSettings();
        if (json is null) return defaults;
        return new ReaderSettings
        {
            Font = ReaderSettingNames.FontFromId(Text(json["font"])) ?? defaults.Font,
            FontSize = Number(json["fontSize"]) ?? defaults.FontSize,
            ContentWidth = Number(json["contentWidth"]) ?? defaults.ContentWidth,
            LineHeight = Number(json["lineHeight"]) ?? defaults.LineHeight,
            Theme = ReaderSettingNames.ThemeFromId(Text(json["theme"])) ?? defaults.Theme,
        };
    }

    public static ReaderSettings FromJson(string? json)
    {
        if (json is null) return new ReaderSettings();
        try
        {
            return FromJsonObject(JsonNode.Parse(json) as JsonObject);
        }
        catch (JsonException)
        {
            return new ReaderSettings();
        }
    }

    /// <summary>These settings with the fields present in <paramref name="changes"/> replaced (automation's <c>readerSettings</c>).</summary>
    public ReaderSettings Merging(JsonObject changes)
    {
        var merged = ToJsonObject();
        foreach (var (key, value) in changes) merged[key] = value?.DeepClone();
        return FromJsonObject(merged);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<long>(out var big)) return (int)Math.Clamp(big, int.MinValue, int.MaxValue);
        if (value.TryGetValue<double>(out var real) && double.IsFinite(real)) return (int)Math.Round(Math.Clamp(real, int.MinValue, int.MaxValue));
        return null;
    }
}
