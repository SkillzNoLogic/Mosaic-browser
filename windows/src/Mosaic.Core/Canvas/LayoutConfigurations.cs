using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Canvas.Interim;

// Named configurations (a layout plus its appearance, saved under a name), their library, and
// .json export/import with validation, ported from macOS
// (Sources/MosaicCore/Canvas/LayoutConfigurations.swift). The files are the macOS app's
// exactly (same keys, ISO 8601 dates without fractions, which macOS requires), so they move
// between the platforms both ways. Importing never crashes and never throws away the user's
// work: unreadable files are refused with a reason; readable ones are repaired and every repair
// is reported. Owner: canvas.

namespace Mosaic.Core.Canvas;

/// <summary>A saved layout + appearance.</summary>
public sealed record LayoutConfiguration(string Id, string Name, DateTimeOffset Created, DateTimeOffset Modified, MosaicLayout Layout)
{
    public static LayoutConfiguration Make(string name, MosaicLayout layout, DateTimeOffset now) =>
        new(Guid.NewGuid().ToString().ToUpperInvariant(), name, now, now, layout);
}

/// <summary>ISO 8601 as Swift's <c>.iso8601</c> strategy writes and reads it.</summary>
public static class Iso8601
{
    public static string Write(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? Read(string? text)
    {
        if (text is null) return null;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
        return DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    /// <summary>Whole seconds, as a round trip through the file keeps them.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset date) =>
        new(date.Ticks - date.Ticks % TimeSpan.TicksPerSecond, date.Offset);
}

/// <summary>The user's saved configurations, in the order they were made.</summary>
public sealed class ConfigurationLibrary
{
    public const int MaxConfigurations = 200;
    public const int MaxNameLength = 60;

    private readonly List<LayoutConfiguration> configurations = [];

    public IReadOnlyList<LayoutConfiguration> Configurations => configurations;

    /// <summary>The configuration last applied or saved.</summary>
    public string? ActiveID { get; set; }

    public ConfigurationLibrary(IEnumerable<LayoutConfiguration>? configurations = null, string? activeID = null)
    {
        if (configurations is not null) this.configurations.AddRange(configurations);
        ActiveID = activeID;
    }

    public LayoutConfiguration? Configuration(string id) => configurations.FirstOrDefault(c => c.Id == id);

    /// <summary>By ID, or by name ignoring case.</summary>
    public LayoutConfiguration? Find(string key) =>
        Configuration(key) ?? configurations.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Trimmed, without control characters, at most <see cref="MaxNameLength"/> characters; null if empty.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        var builder = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            builder.Append(rune.ToString());
        }
        var cleaned = builder.ToString().Trim();
        if (cleaned.Length == 0) return null;
        // Characters as people see them (grapheme clusters), as Swift's prefix counts.
        var elements = StringInfo.GetTextElementEnumerator(cleaned);
        var result = new StringBuilder();
        var count = 0;
        while (count < MaxNameLength && elements.MoveNext())
        {
            result.Append(elements.GetTextElement());
            count++;
        }
        return result.ToString();
    }

    /// <summary><paramref name="name"/>, or "name 2", "name 3"… so it is unique.</summary>
    public string UniqueName(string name, string? excluding = null)
    {
        var b = CleanName(name) ?? "Configuration";
        bool Taken(string candidate) => configurations.Any(c => c.Id != excluding && string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase));
        if (!Taken(b)) return b;
        var n = 2;
        while (Taken($"{b} {n}")) n++;
        return $"{b} {n}";
    }

    /// <summary>Saves <paramref name="layout"/> under a unique name and makes it active; null when full.</summary>
    public LayoutConfiguration? Save(string name, MosaicLayout layout, DateTimeOffset? now = null)
    {
        if (configurations.Count >= MaxConfigurations) return null;
        var stored = LayoutCanvasRules.Usable(layout);
        var unique = UniqueName(name);
        stored.Name = unique;
        var when = Iso8601.Truncate(now ?? DateTimeOffset.UtcNow);
        var configuration = LayoutConfiguration.Make(unique, stored, when);
        configurations.Add(configuration);
        ActiveID = configuration.Id;
        return configuration;
    }

    public bool Update(string id, MosaicLayout layout, DateTimeOffset? now = null)
    {
        var index = configurations.FindIndex(c => c.Id == id);
        if (index < 0) return false;
        var stored = LayoutCanvasRules.Usable(layout);
        stored.Name = configurations[index].Name;
        configurations[index] = configurations[index] with { Layout = stored, Modified = Iso8601.Truncate(now ?? DateTimeOffset.UtcNow) };
        ActiveID = id;
        return true;
    }

    public bool Rename(string id, string name, DateTimeOffset? now = null)
    {
        var index = configurations.FindIndex(c => c.Id == id);
        if (index < 0 || CleanName(name) is null) return false;
        var unique = UniqueName(name, id);
        var layout = configurations[index].Layout.Clone();
        layout.Name = unique;
        configurations[index] = configurations[index] with { Name = unique, Layout = layout, Modified = Iso8601.Truncate(now ?? DateTimeOffset.UtcNow) };
        return true;
    }

    public bool Delete(string id)
    {
        var index = configurations.FindIndex(c => c.Id == id);
        if (index < 0) return false;
        configurations.RemoveAt(index);
        if (ActiveID == id) ActiveID = null;
        return true;
    }

    // MARK: Persistence (the macOS file: Canvas/configurations.json)

    public string ToJson()
    {
        var root = new JsonObject();
        if (ActiveID is not null) root["activeID"] = ActiveID;
        root["configurations"] = new JsonArray([.. configurations.Select(c => (JsonNode)new JsonObject
        {
            ["created"] = Iso8601.Write(c.Created),
            ["id"] = c.Id,
            ["layout"] = c.Layout.ToJsonObject(),
            ["modified"] = Iso8601.Write(c.Modified),
            ["name"] = c.Name,
        })]);
        return SwiftJson.Write(root);
    }

    /// <summary>The library in a file's text; one unreadable entry never loses the others, junk gives an empty library.</summary>
    public static ConfigurationLibrary FromJson(string text)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (Exception) { return new ConfigurationLibrary(); }
        if (root is not JsonObject o) return new ConfigurationLibrary();
        var entries = new List<LayoutConfiguration>();
        if (o["configurations"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject e
                    || SwiftDecode.TryString(e["id"]) is not { } id
                    || SwiftDecode.TryString(e["name"]) is not { } name
                    || Iso8601.Read(SwiftDecode.TryString(e["created"])) is not { } created
                    || Iso8601.Read(SwiftDecode.TryString(e["modified"])) is not { } modified
                    || MosaicLayout.Decode(e["layout"]) is not { } layout)
                    continue;
                entries.Add(new LayoutConfiguration(id, CleanName(name) ?? "Configuration", created, modified, LayoutCanvasRules.Usable(layout)));
            }
        }
        var library = new ConfigurationLibrary(entries, SwiftDecode.TryString(o["activeID"]));
        if (library.ActiveID is { } active && library.Configuration(active) is null) library.ActiveID = null;
        return library;
    }

    public static ConfigurationLibrary Load(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : new ConfigurationLibrary(); }
        catch (Exception) { return new ConfigurationLibrary(); }
    }

    /// <summary>Writes atomically (a temporary file moved over the old one).</summary>
    public static void WriteFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public ConfigurationLibrary Copy() => new(configurations, ActiveID);
}

/// <summary>Export and import of layout files.</summary>
public static class LayoutTransfer
{
    public const string Format = "dev.mosaic.layout";
    public const int Version = 1;
    /// <summary>Larger files are refused before parsing (a layout is a few kilobytes).</summary>
    public const int MaxFileSize = 512 * 1024;

    public static string Export(string name, MosaicLayout layout, DateTimeOffset? now = null)
    {
        var stored = LayoutCanvasRules.Usable(layout);
        var clean = ConfigurationLibrary.CleanName(name) ?? stored.Name;
        stored.Name = clean;
        return SwiftJson.Write(new JsonObject
        {
            ["exported"] = Iso8601.Write(now ?? DateTimeOffset.UtcNow),
            ["format"] = Format,
            ["layout"] = stored.ToJsonObject(),
            ["name"] = clean,
            ["version"] = Version,
        });
    }

    public enum ImportErrorKind { Empty, TooLarge, NotJSON, NotALayout }

    public sealed record ImportError(ImportErrorKind Kind, long Bytes = 0)
    {
        public string Description => Kind switch
        {
            ImportErrorKind.Empty => "The file is empty.",
            ImportErrorKind.TooLarge => $"The file is too large to be a layout ({Bytes / 1024} KB; layouts are a few KB).",
            ImportErrorKind.NotJSON => "The file isn’t valid JSON.",
            _ => "The file doesn’t contain a Mosaic layout.",
        };
    }

    public sealed record ImportReport(MosaicLayout Layout, string Name, IReadOnlyList<string> Messages);

    /// <summary>The report, or the error (exactly one is non-null).</summary>
    public sealed record ImportResult(ImportReport? Report, ImportError? Error);

    private static ImportResult Fail(ImportErrorKind kind, long bytes = 0) => new(null, new ImportError(kind, bytes));

    public static ImportResult ImportLayout(string text) => ImportLayout(Encoding.UTF8.GetBytes(text));

    /// <summary>Reads a layout file (exported by Mosaic, or a bare layout), repairing what it can and reporting every repair.</summary>
    public static ImportResult ImportLayout(byte[] data)
    {
        if (data.Length == 0) return Fail(ImportErrorKind.Empty);
        if (data.Length > MaxFileSize) return Fail(ImportErrorKind.TooLarge, data.Length);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { MaxDepth = 256 });
            root?.ToJsonString();  // forces validation of lazily parsed values (duplicate keys and the like)
        }
        catch (Exception) { return Fail(ImportErrorKind.NotJSON); }
        if (root is not JsonObject obj) return Fail(ImportErrorKind.NotALayout);

        var messages = new List<string>();
        JsonObject raw;
        string? name = null;
        if (obj["layout"] is JsonObject layoutObject)
        {
            raw = layoutObject;
            name = SwiftDecode.TryString(obj["name"]);
            if (SwiftDecode.TryString(obj["format"]) is { } fmt && fmt != Format)
                messages.Add($"The file says it is “{fmt}”, not a Mosaic layout; it was read as one anyway.");
            if (SwiftDecode.TryDouble(obj["version"]) is { } v && v == Math.Floor(v) && v > Version)
                messages.Add("Written by a newer Mosaic: settings this version doesn’t know were ignored.");
        }
        else if (obj.ContainsKey("regions") || obj.ContainsKey("preset") || obj.ContainsKey("tabPlacement"))
        {
            raw = obj;
        }
        else
        {
            return Fail(ImportErrorKind.NotALayout);
        }
        if (SwiftDecode.TryString(raw["preset"]) is { } preset && JsonEnum.Parse<MosaicPresetID>(preset) is null)
        {
            // Windows has no Safari Mosaic: the shell reads a Safari layout as Microsoft Edge, keeping its look.
            messages.Add(preset == "safari"
                ? "A Safari layout from a Mac: Windows shows it as Microsoft Edge, keeping its colours and backgrounds."
                : $"Unknown preset “{preset}”: started from Chrome.");
        }
        if (raw.ContainsKey("regions"))
        {
            if (raw["regions"] is JsonObject dict)
            {
                var unknownComponents = new List<string>();
                var unknownRegions = new List<string>();
                var unreadable = false;
                foreach (var (key, value) in dict.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (JsonEnum.Parse<ShellRegion>(key) is null) { unknownRegions.Add(key); continue; }
                    if (value is not JsonArray names) { unreadable = true; continue; }
                    foreach (var item in names)
                    {
                        if (item is not JsonValue sv || sv.GetValueKind() != JsonValueKind.String) { unreadable = true; continue; }
                        var text = sv.GetValue<string>();
                        if (JsonEnum.Parse<ShellComponent>(text) is null) unknownComponents.Add(text);
                    }
                }
                if (unknownComponents.Count > 0)
                    messages.Add($"Dropped unknown components: {string.Join(", ", unknownComponents.Select(c => $"“{c}”"))}.");
                if (unknownRegions.Count > 0)
                    messages.Add($"Ignored unknown regions: {string.Join(", ", unknownRegions.Select(r => $"“{r}”"))}.");
                if (unreadable) messages.Add("Some region entries weren’t lists of names and were skipped.");
            }
            else
            {
                messages.Add("The regions couldn’t be read; the preset’s arrangement was used.");
            }
        }

        // Decode the sanitised object (forgiving: unknown values fall back to the preset's).
        var sanitized = (JsonObject)raw.DeepClone();
        if (raw["regions"] is JsonObject regions)
        {
            var clean = new JsonObject();
            foreach (var (key, value) in regions)
            {
                if (JsonEnum.Parse<ShellRegion>(key) is null || value is not JsonArray names) continue;
                clean[key] = new JsonArray([.. names
                    .Where(i => i is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                    .Select(i => i!.GetValue<string>())
                    .Where(t => JsonEnum.Parse<ShellComponent>(t) is not null)
                    .Select(t => (JsonNode)JsonValue.Create(t)!)]);
            }
            sanitized["regions"] = clean;
        }
        if (MosaicLayout.Decode(sanitized) is not { } decoded) return Fail(ImportErrorKind.NotALayout);

        var layout = LayoutCanvasRules.Usable(decoded);
        messages.AddRange(Repairs(decoded, layout));
        var finalName = ConfigurationLibrary.CleanName(name) ?? ConfigurationLibrary.CleanName(layout.Name) ?? layout.Preset.Title();
        layout.Name = finalName;
        return new ImportResult(new ImportReport(layout, finalName, messages), null);
    }

    /// <summary>Human descriptions of what <c>Usable</c> changed.</summary>
    internal static List<string> Repairs(MosaicLayout decoded, MosaicLayout layout)
    {
        var messages = new List<string>();
        var regions = Enum.GetValues<ShellRegion>();
        var components = Enum.GetValues<ShellComponent>();
        var before = regions.SelectMany(r => decoded.Components(r).Where(c => r.Accepts(c))).ToList();
        var duplicates = before.Where(c => c != ShellComponent.FlexibleSpace).GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            messages.Add($"Removed duplicates of: {string.Join(", ", duplicates.Select(c => c.Title()).OrderBy(t => t, StringComparer.Ordinal))}.");
        var misplaced = regions.SelectMany(r => decoded.Components(r).Where(c => !r.Accepts(c))).Distinct().ToList();
        if (misplaced.Count > 0)
            messages.Add($"Removed components from rows that can’t hold them: {string.Join(", ", misplaced.Select(c => c.Title()).OrderBy(t => t, StringComparer.Ordinal))}.");
        var added = components.Where(c => layout.Contains(c) && !decoded.Contains(c)).ToHashSet();
        if (added.Contains(ShellComponent.AddressField)) messages.Add("Added the address field, which every layout needs.");
        if (added.Contains(ShellComponent.TabStrip)) messages.Add("Added the tab strip, which tabs on top need.");
        if (added.Contains(ShellComponent.Menu)) messages.Add("Added the browser menu so Settings stays reachable.");
        if (added.Contains(ShellComponent.SidebarToggle)) messages.Add("Added the sidebar button, which a collapsible sidebar needs.");
        if (decoded.RegionOf(ShellComponent.AddressField) != layout.RegionOf(ShellComponent.AddressField) && decoded.Contains(ShellComponent.AddressField))
            messages.Add($"Moved the address field to the {layout.RegionOf(ShellComponent.AddressField)?.Title() ?? "toolbar"} so it stays visible.");

        static bool Changed(double a, double b) => !double.IsFinite(a) || Math.Abs(a - b) > 0.0001;
        var clamped = new List<string>();
        if (Changed(decoded.SidebarWidth, layout.SidebarWidth)) clamped.Add("sidebar width");
        var (da, la) = (decoded.Appearance, layout.Appearance);
        if (Changed(da.CornerRadius, la.CornerRadius)) clamped.Add("corner radius");
        if (Changed(da.ContentInset, la.ContentInset)) clamped.Add("page inset");
        if (Changed(da.ContentCornerRadius, la.ContentCornerRadius)) clamped.Add("page corners");
        if (clamped.Count > 0) messages.Add($"Brought out-of-range values back into range: {string.Join(", ", clamped)}.");
        if (da.Accent != la.Accent) messages.Add($"The accent colour “{da.Accent}” isn’t a colour; the system accent is used.");
        if (!Gradients.Same(da.Background, la.Background) || !Gradients.Same(da.NewTabBackground, la.NewTabBackground)
            || !Gradients.SameWorkspaces(da.WorkspaceBackgrounds, la.WorkspaceBackgrounds))
            messages.Add("Repaired the background (invalid colours removed, values brought into range).");
        return messages;
    }
}
