using Mosaic.Core.Browser;

namespace Mosaic.Core.Tabs;

/// <summary>
/// The tabs area's keyboard shortcuts: the macOS ones with Ctrl in place of ⌘ (and Ctrl+Alt in
/// place of ⌃⌘ for workspaces). Keys are Windows virtual-key codes, like <see cref="Shortcuts"/>,
/// so the app can call <see cref="Handle"/> for keys pressed in Mosaic's own controls and for
/// keys WebView2 forwards from a Chromium page. Keys pressed in a Gecko page arrive as agent
/// events ("tabs.reopenClosed", …) and go to <see cref="BrowserModel.HandleTabsEvent"/>.
/// </summary>
public static class TabsShortcuts
{
    private const int Left = 0x25;
    private const int Right = 0x27;

    /// <summary>What a key does, or null. Modifiers must match exactly.</summary>
    public static string? Match(int virtualKey, ShortcutModifiers modifiers) => (modifiers, virtualKey) switch
    {
        (ShortcutModifiers.Control | ShortcutModifiers.Shift, 'T') => "tabs.reopenClosed",
        (ShortcutModifiers.Control | ShortcutModifiers.Shift, 'A') => "tabs.search",
        (ShortcutModifiers.Control, >= '1' and <= '9') => $"tabs.select{(char)virtualKey}",
        (ShortcutModifiers.Control | ShortcutModifiers.Alt, Right) => "workspaces.next",
        (ShortcutModifiers.Control | ShortcutModifiers.Alt, Left) => "workspaces.previous",
        (ShortcutModifiers.Control | ShortcutModifiers.Alt, 'N') => "workspaces.new",
        _ => null,
    };

    /// <summary>Runs the shortcut bound to the key, if any. Returns whether it was one.</summary>
    public static bool Handle(BrowserModel model, int virtualKey, ShortcutModifiers modifiers)
    {
        switch (Match(virtualKey, modifiers))
        {
            case null:
                return false;
            case "tabs.search":
                model.ToggleTabSearch();
                return true;
            case "workspaces.next":
                model.SelectAdjacentWorkspace(1);
                return true;
            case "workspaces.previous":
                model.SelectAdjacentWorkspace(-1);
                return true;
            case "workspaces.new":
                model.CreateWorkspace();
                return true;
            case var tabs:
                model.HandleTabsEvent(tabs);
                return true;
        }
    }
}
