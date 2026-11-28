using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Browser;

/// <summary>
/// The few preferences Mosaic remembers between runs, kept as a small JSON file
/// (<c>settings.json</c> under <see cref="AppPaths.DataRoot"/>).
/// </summary>
/// <remarks>
/// Preferences are a convenience, never a reason to fail: an unreadable file means defaults, and
/// a file that cannot be written means the choice lasts for this run only.
/// </remarks>
public sealed class BrowserSettings
{
    private const string SearchEngineKey = "searchEngine";
    private const string EngineKey = "engine";
    private const string ChromiumExtensionsKey = "chromiumExtensions";
    private const string BlockerEnabledKey = "blockerEnabled";
    private const string BlockerAllowlistKey = "blockerAllowlist";
    private const string WindowPlacementKey = "windowPlacement";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string path;
    private readonly Dictionary<string, string> chromiumExtensionIds = new(StringComparer.Ordinal);
    /// <summary>The file as read, so keys this class does not know survive a save.</summary>
    private readonly JsonObject stored = [];

    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "settings.json");

    /// <summary>Reads the settings at <paramref name="path"/>; a missing or damaged file gives defaults.</summary>
    public BrowserSettings(string path)
    {
        this.path = path;
        if (ReadFile(path) is not { } stored) return;
        this.stored = stored;

        SearchEngineId = StringAt(stored, SearchEngineKey);
        Engine = EngineKindExtensions.FromId(StringAt(stored, EngineKey));
        if (stored[ChromiumExtensionsKey] is JsonObject ids)
        {
            foreach (var (chromeId, value) in ids)
            {
                if (value is JsonValue text && text.TryGetValue(out string? chromiumId))
                {
                    chromiumExtensionIds[chromeId] = chromiumId;
                }
            }
        }
        if (stored[BlockerEnabledKey] is JsonValue enabled && enabled.TryGetValue(out bool isEnabled)) BlockerEnabled = isEnabled;
        if (stored[BlockerAllowlistKey] is JsonArray sites)
        {
            BlockerAllowlist = [.. sites.OfType<JsonValue>().Select(site => site.TryGetValue(out string? text) ? text : null).OfType<string>()];
        }
        WindowPlacement = WindowPlacement.FromJson(stored[WindowPlacementKey]);
    }

    /// <summary><see cref="Search.SearchEngine.Id"/> of the chosen search engine; null if never chosen.</summary>
    public string? SearchEngineId { get; set; }

    /// <summary>The engine Mosaic starts with: the one last picked with the engine switch.</summary>
    public EngineKind? Engine { get; set; }

    /// <summary>
    /// Chromium's own ID for each extension Mosaic has loaded into the Chromium profile, by Chrome
    /// Web Store ID. The two differ for unpacked extensions, and the profile keeps extensions
    /// between runs, so this is how Mosaic recognises its own extensions next time.
    /// </summary>
    public IReadOnlyDictionary<string, string> ChromiumExtensionIds => chromiumExtensionIds;

    /// <summary>The content blocker's master switch; null if never switched, which means on.</summary>
    public bool? BlockerEnabled { get; set; }

    /// <summary>Sites the content blocker is switched off for (see <see cref="Blocker.BlockerSettings"/>).</summary>
    public IReadOnlyList<string> BlockerAllowlist { get; set; } = [];

    /// <summary>Where the window was when Mosaic last closed (physical pixels); null if never saved.</summary>
    public WindowPlacement? WindowPlacement { get; set; }

    public void SetChromiumExtensionId(string chromeId, string? chromiumId)
    {
        if (chromiumId is null) chromiumExtensionIds.Remove(chromeId);
        else chromiumExtensionIds[chromeId] = chromiumId;
    }

    /// <summary>Writes the file, replacing it in one step so a crash cannot leave half a file.</summary>
    public void Save()
    {
        // Keys this version does not know (written by a newer Mosaic, or by a feature area's own
        // code) are kept as they were.
        var root = (JsonObject)stored.DeepClone();
        foreach (var key in new[] { SearchEngineKey, EngineKey, ChromiumExtensionsKey, BlockerEnabledKey, BlockerAllowlistKey, WindowPlacementKey })
        {
            root.Remove(key);
        }
        if (SearchEngineId is not null) root[SearchEngineKey] = SearchEngineId;
        if (Engine is { } engine) root[EngineKey] = engine.Id();
        if (chromiumExtensionIds.Count > 0)
        {
            var ids = new JsonObject();
            foreach (var (chromeId, chromiumId) in chromiumExtensionIds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                ids[chromeId] = chromiumId;
            }
            root[ChromiumExtensionsKey] = ids;
        }
        if (BlockerEnabled is { } blockerEnabled) root[BlockerEnabledKey] = blockerEnabled;
        if (BlockerAllowlist.Count > 0)
        {
            root[BlockerAllowlistKey] = new JsonArray([.. BlockerAllowlist.Order(StringComparer.Ordinal).Select(site => JsonValue.Create(site))]);
        }
        if (WindowPlacement is { } placement) root[WindowPlacementKey] = placement.ToJson();

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(Indented));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // See the class remarks: the preference simply does not outlive this run.
        }
    }

    private static JsonObject? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? StringAt(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}

/// <summary>
/// The main window's restored bounds in physical pixels (virtual-screen coordinates) and whether
/// it was maximised.
/// </summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    internal JsonObject ToJson() => new()
    {
        ["x"] = X,
        ["y"] = Y,
        ["width"] = Width,
        ["height"] = Height,
        ["maximized"] = Maximized,
    };

    internal static WindowPlacement? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json) return null;
        int? Number(string key) => json[key] is JsonValue value && value.TryGetValue(out int number) ? number : null;
        if (Number("x") is not { } x || Number("y") is not { } y || Number("width") is not { } width || Number("height") is not { } height) return null;
        if (width <= 0 || height <= 0) return null;
        bool maximized = json["maximized"] is JsonValue flag && flag.TryGetValue(out bool isMaximized) && isMaximized;
        return new WindowPlacement(x, y, width, height, maximized);
    }
}
