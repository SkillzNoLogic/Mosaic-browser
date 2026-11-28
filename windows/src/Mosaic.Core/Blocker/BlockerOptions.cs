using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Blocker;

/// <summary>
/// The content blocker's choices beyond the master switch and allowlist (which
/// <see cref="Browser.BrowserSettings"/> keeps): cosmetic filtering and which filter lists are on.
/// Kept as <c>Blocker\options.json</c> under <see cref="AppPaths.DataRoot"/>, with macOS's
/// preference names (<c>blockerCosmetic</c>, <c>blockerLists</c>).
/// </summary>
public sealed record BlockerOptions
{
    /// <summary>Hide page elements named by cosmetic filters (<c>##.ad</c>).</summary>
    public bool CosmeticEnabled { get; init; } = true;

    /// <summary>IDs of the filter lists in use (see <see cref="FilterListCatalog.All"/>); uBO's defaults unless changed.</summary>
    public IReadOnlySet<string> EnabledListIds { get; init; } = FilterListCatalog.Defaults.Select(list => list.Id).ToHashSet(StringComparer.Ordinal);

    public static string DefaultPath => PathFor(AppPaths.BlockerLists);

    /// <summary>Where the options for the lists in <paramref name="listsDirectory"/> are kept: beside that folder.</summary>
    public static string PathFor(string listsDirectory) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(listsDirectory))) ?? listsDirectory, "options.json");

    /// <summary>The lists in use, in catalogue order.</summary>
    public IReadOnlyList<FilterList> EnabledLists => FilterListCatalog.ListsEnabled(EnabledListIds);

    public BlockerOptions WithList(string id, bool enabled)
    {
        var ids = new HashSet<string>(EnabledListIds, StringComparer.Ordinal);
        if (enabled) ids.Add(id);
        else ids.Remove(id);
        return this with { EnabledListIds = ids };
    }

    /// <summary>Reads the options; a missing or damaged file gives defaults. Unknown list IDs are dropped.</summary>
    public static BlockerOptions Load(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json) return new();
            var options = new BlockerOptions();
            if (json["blockerCosmetic"] is JsonValue cosmetic && cosmetic.TryGetValue(out bool on)) options = options with { CosmeticEnabled = on };
            if (json["blockerLists"] is JsonArray lists)
            {
                var known = FilterListCatalog.All.Select(list => list.Id).ToHashSet(StringComparer.Ordinal);
                options = options with
                {
                    EnabledListIds = lists.OfType<JsonValue>().Select(value => value.TryGetValue(out string? id) ? id : null)
                        .OfType<string>().Where(known.Contains).ToHashSet(StringComparer.Ordinal),
                };
            }
            return options;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Writes the options in one step. A file that cannot be written means the choice lasts for this run only.</summary>
    public void Save(string path)
    {
        var json = new JsonObject
        {
            ["blockerCosmetic"] = CosmeticEnabled,
            ["blockerLists"] = new JsonArray([.. EnabledListIds.Order(StringComparer.Ordinal).Select(id => JsonValue.Create(id))]),
        };
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".tmp", json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public bool Equals(BlockerOptions? other) =>
        other is not null && CosmeticEnabled == other.CosmeticEnabled && EnabledListIds.SetEquals(other.EnabledListIds);

    public override int GetHashCode() => HashCode.Combine(CosmeticEnabled, EnabledListIds.Count);
}
