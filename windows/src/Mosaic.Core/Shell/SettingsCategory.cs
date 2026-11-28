namespace Mosaic.Core.Shell;

/// <summary>
/// The categories of Settings, shown inside the main window (never a second window).
/// Same set and order as <c>Sources/Mosaic/Settings</c> on macOS. Each area supplies its
/// page through the app's <c>FeatureMounts.SettingsPages</c>.
/// </summary>
public enum SettingsCategory
{
    Mosaics, General, Accessibility, TabsAndWorkspaces, Performance, Search, Privacy,
    Passwords, Downloads, Engine, Extensions, Languages, Shortcuts, About,
}

public static class SettingsCategories
{
    /// <summary>Stable id used by automation (<c>openSettings &lt;id&gt;</c>) and saved state; matches macOS raw values.</summary>
    public static string Id(this SettingsCategory category) => category switch
    {
        SettingsCategory.Mosaics => "mosaics",
        SettingsCategory.General => "general",
        SettingsCategory.Accessibility => "accessibility",
        SettingsCategory.TabsAndWorkspaces => "tabs",
        SettingsCategory.Performance => "performance",
        SettingsCategory.Search => "search",
        SettingsCategory.Privacy => "privacy",
        SettingsCategory.Passwords => "passwords",
        SettingsCategory.Downloads => "downloads",
        SettingsCategory.Engine => "engine",
        SettingsCategory.Extensions => "extensions",
        SettingsCategory.Languages => "languages",
        SettingsCategory.Shortcuts => "shortcuts",
        SettingsCategory.About => "about",
        _ => category.ToString().ToLowerInvariant(),
    };

    public static string Title(this SettingsCategory category) => category switch
    {
        SettingsCategory.TabsAndWorkspaces => "Tabs & Workspaces",
        SettingsCategory.Privacy => "Privacy & Security",
        SettingsCategory.Passwords => "Passwords & Autofill",
        SettingsCategory.Shortcuts => "Shortcuts & Gestures",
        SettingsCategory.About => "About & Updates",
        _ => category.ToString(),
    };

    /// <summary>The area that owns the page (see windows/AGENTS.md).</summary>
    public static string Owner(this SettingsCategory category) => category switch
    {
        SettingsCategory.Mosaics or SettingsCategory.General or SettingsCategory.Accessibility => "shell",
        SettingsCategory.TabsAndWorkspaces or SettingsCategory.Performance => "tabs",
        SettingsCategory.Search => "library",
        SettingsCategory.Privacy => "privacy",
        SettingsCategory.Passwords => "passwords",
        SettingsCategory.Downloads => "downloads",
        SettingsCategory.Shortcuts => "productivity",
        _ => "platform",
    };

    public static SettingsCategory? FromId(string id) =>
        Enum.GetValues<SettingsCategory>().Cast<SettingsCategory?>().FirstOrDefault(c => c!.Value.Id() == id);
}
