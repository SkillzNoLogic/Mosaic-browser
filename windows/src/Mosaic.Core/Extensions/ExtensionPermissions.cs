using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Extensions;

/// <summary>
/// What an extension asked for, read from its converted (Gecko) manifest, for Settings › Extensions.
/// Port of <c>ExtensionManager.Permissions</c> in <c>Sources/Mosaic/Model/ExtensionManager.swift</c>.
/// </summary>
/// <param name="Apis">API permissions ("storage", "tabs"…), sorted.</param>
/// <param name="Sites">Sites it can read and change, as match patterns; "&lt;all_urls&gt;" for every site.</param>
public sealed record ExtensionPermissions(IReadOnlyList<string> Apis, IReadOnlyList<string> Sites)
{
    public static readonly ExtensionPermissions None = new([], []);

    public bool ReadsAllSites => Sites.Any(site => site == "<all_urls>" || site.StartsWith("*://*/", StringComparison.Ordinal)
        || site.StartsWith("http://*/", StringComparison.Ordinal) || site.StartsWith("https://*/", StringComparison.Ordinal));

    /// <summary>"Can read and change all websites" and so on: the lines the settings page shows.</summary>
    public IReadOnlyList<string> Summary
    {
        get
        {
            var lines = new List<string>();
            if (ReadsAllSites) lines.Add("Can read and change all websites");
            else if (Sites.Count > 0)
            {
                lines.Add("Can read and change: " + string.Join(", ", Sites.Take(6)) + (Sites.Count > 6 ? $" and {Sites.Count - 6} more" : ""));
            }
            if (Apis.Count > 0) lines.Add("Uses: " + string.Join(", ", Apis));
            if (lines.Count == 0) lines.Add("Asks for no special permissions");
            return lines;
        }
    }

    /// <summary>Reads <c>manifest.json</c> in <paramref name="extensionDirectory"/>; <see cref="None"/> if it cannot be read.</summary>
    public static ExtensionPermissions Read(string extensionDirectory)
    {
        try
        {
            var path = Path.Combine(extensionDirectory, "manifest.json");
            return File.Exists(path) ? FromManifest(File.ReadAllText(path)) : None;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return None;
        }
    }

    public static ExtensionPermissions FromManifest(string json)
    {
        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return None;
        }
        if (manifest is null) return None;
        var apis = new SortedSet<string>(StringComparer.Ordinal);
        var sites = new List<string>();
        foreach (var entry in Strings(manifest["permissions"]).Concat(Strings(manifest["host_permissions"])))
        {
            if (entry == "<all_urls>" || entry.Contains("://", StringComparison.Ordinal)) sites.Add(entry);
            else apis.Add(entry);
        }
        if (manifest["content_scripts"] is JsonArray scripts)
        {
            foreach (var script in scripts.OfType<JsonObject>()) sites.AddRange(Strings(script["matches"]));
        }
        return new ExtensionPermissions([.. apis], [.. sites.Distinct(StringComparer.Ordinal)]);
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonValue>().Select(value => value.TryGetValue(out string? text) ? text : null).OfType<string>()
            : [];
}
