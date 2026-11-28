namespace Mosaic.Core.Library;

/// <summary>
/// The library's keyboard shortcuts (Windows conventions): Ctrl+D bookmark this page,
/// Ctrl+Shift+O bookmarks manager, Ctrl+H history, Ctrl+Shift+B show or hide the bookmarks bar.
/// </summary>
/// <remarks>
/// Mosaic's own controls get them from the keyboard accelerators the library registers; a Gecko
/// page forwards them through the agent (<c>Resources/gecko/agent.sys.mjs</c>); a Chromium page
/// reports them as WebView2 accelerator keys, which the winui area can pass to
/// <see cref="Match"/> and then <see cref="BrowserLibrary.HandleCommand"/>.
/// </remarks>
public static class LibraryShortcuts
{
    /// <summary>The command for a key press (Windows virtual-key code), or null.</summary>
    public static string? Match(int virtualKey, bool control, bool shift, bool alt = false)
    {
        if (!control || alt) return null;
        return (char)virtualKey switch
        {
            'D' when !shift => "bookmarkPage",
            'H' when !shift => "showHistory",
            'O' when shift => "showBookmarks",
            'B' when shift => "toggleBookmarksBar",
            _ => null,
        };
    }

    /// <summary>The shortcuts as text, by command (for menus and help).</summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["bookmarkPage"] = "Ctrl+D",
        ["showBookmarks"] = "Ctrl+Shift+O",
        ["showHistory"] = "Ctrl+H",
        ["toggleBookmarksBar"] = "Ctrl+Shift+B",
    };
}
