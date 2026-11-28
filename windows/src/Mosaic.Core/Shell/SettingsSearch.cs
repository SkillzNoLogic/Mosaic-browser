namespace Mosaic.Core.Shell;

/// <summary>
/// Words that find a settings category from the search field: its title plus the labels of
/// its settings. Port of <c>Sources/Mosaic/Settings/SettingsSearch.swift</c> with the Windows
/// vocabulary (Mica, Acrylic, Edge; no Safari or Keychain). Other areas add their own labels
/// with <see cref="AddLabels"/> from their module initialiser.
/// </summary>
/// <remarks>Owner: shell.</remarks>
public static class SettingsSearch
{
    /// <summary>Category order in the settings list, in its three groups (as on macOS).</summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsCategory>> Groups =
    [
        [SettingsCategory.Mosaics, SettingsCategory.General, SettingsCategory.TabsAndWorkspaces, SettingsCategory.Search, SettingsCategory.Engine],
        [SettingsCategory.Privacy, SettingsCategory.Passwords, SettingsCategory.Downloads, SettingsCategory.Extensions],
        [SettingsCategory.Shortcuts, SettingsCategory.Languages, SettingsCategory.Accessibility, SettingsCategory.Performance, SettingsCategory.About],
    ];

    /// <summary>All categories in list order.</summary>
    public static IEnumerable<SettingsCategory> Ordered => Groups.SelectMany(g => g);

    private static readonly Dictionary<SettingsCategory, List<string>> labels = new()
    {
        [SettingsCategory.Mosaics] =
        [
            "Preset", "Zen / Arc", "Chrome", "Opera", "Opera GX", "Microsoft Edge", "Preview", "Restore Defaults",
            "Tab placement", "Vertical tabs", "Horizontal tabs", "Sidebar", "Tab style", "Address bar", "Bookmarks bar",
            "Appearance", "Light", "Dark", "Theme", "Accent colour", "Accent color", "Density", "Corner radius",
            "Page inset", "Materials", "Mica", "Acrylic", "Transparency", "Background", "Gradient", "Glow", "Layout",
            // canvas
            "Customise Layout", "Toolbar buttons", "Drag and drop", "Gradient Studio", "New Tab background",
            "Workspace background", "Configurations", "Import layout", "Export layout",
        ],
        [SettingsCategory.General] = ["Startup", "Homepage", "Home button", "New tabs open with", "New Tab page"],
        [SettingsCategory.Accessibility] =
        [
            "Reduce motion", "Animation effects", "Reduce transparency", "Transparency effects", "Text size", "Larger text",
            "Focus rings", "Keyboard navigation", "Tab close buttons", "Narrator", "High contrast", "Contrast themes",
        ],
        // Other areas: the obvious words until their owners add their own.
        [SettingsCategory.Engine] = ["Rendering engine", "Mozilla", "Gecko", "Chromium", "Blink", "WebView2", "Default engine", "Firefox"],
        [SettingsCategory.TabsAndWorkspaces] = ["Tabs", "Workspaces", "Pinned tabs", "Tab groups", "Session restore", "Recently closed"],
        [SettingsCategory.Search] = ["Search engine", "Suggestions", "Google", "DuckDuckGo", "Bing", "Brave", "Kagi"],
        [SettingsCategory.Privacy] =
        [
            "Content blocker", "Ad blocker", "Trackers", "Cookies", "Site data", "Permissions",
            "HTTPS", "Clear browsing data", "Pop-ups",
        ],
        [SettingsCategory.Passwords] = ["Passwords", "Autofill", "Credential Manager", "Windows Hello", "Password generator"],
        [SettingsCategory.Downloads] = ["Downloads", "Download folder", "Ask where to save", "Free Download Manager"],
        [SettingsCategory.Extensions] = ["Extensions", "Chrome Web Store", "Add-ons", "crx"],
        [SettingsCategory.Shortcuts] = ["Keyboard shortcuts", "Command palette", "Gestures"],
        [SettingsCategory.Languages] = ["Languages", "Spelling", "Translate"],
        [SettingsCategory.Performance] = ["Performance", "Memory", "Sleeping tabs", "Unload tabs"],
        [SettingsCategory.About] = ["About", "Version", "Updates", "Automatic updates", "Install update", "Licences", "Licenses"],
    };

    public static IReadOnlyList<string> Labels(SettingsCategory category)
    {
        lock (labels) return labels.TryGetValue(category, out var list) ? [.. list] : [];
    }

    /// <summary>Adds search words for a category (each area for its own page).</summary>
    public static void AddLabels(SettingsCategory category, params string[] words)
    {
        lock (labels)
        {
            if (!labels.TryGetValue(category, out var list)) labels[category] = list = [];
            foreach (var word in words)
            {
                if (!list.Contains(word, StringComparer.OrdinalIgnoreCase)) list.Add(word);
            }
        }
    }

    /// <summary>A matching category and the setting labels that matched (empty when only the title did).</summary>
    public sealed record Match(SettingsCategory Category, IReadOnlyList<string> Labels);

    /// <summary>
    /// Categories matching <paramref name="query"/> (every word must appear in the title or one
    /// label), in list order. An empty query matches every category.
    /// </summary>
    public static IReadOnlyList<Match> Search(string? query)
    {
        var words = (query ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [.. Ordered.Select(c => new Match(c, []))];
        var result = new List<Match>();
        foreach (var category in Ordered)
        {
            var title = category.Title().ToLowerInvariant();
            var all = Labels(category);
            var matching = all.Where(label =>
            {
                var lower = label.ToLowerInvariant();
                return words.All(w => lower.Contains(w, StringComparison.Ordinal) || title.Contains(w, StringComparison.Ordinal));
            }).ToList();
            var titleMatches = words.All(w => title.Contains(w, StringComparison.Ordinal));
            if (!titleMatches && matching.Count == 0) continue;
            result.Add(new Match(category, titleMatches && matching.Count == all.Count ? [] : matching));
        }
        return result;
    }
}
