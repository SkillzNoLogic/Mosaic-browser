namespace Mosaic.Core.Browser;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// Mosaic's keyboard shortcuts: the macOS ones with Ctrl in place of ⌘.
/// </summary>
/// <remarks>
/// Keys are Windows virtual-key codes because both sources of key presses speak them: WPF (via
/// <c>KeyInterop</c>) while Mosaic's own controls have focus, and WebView2's accelerator-key
/// forwarding while a Chromium page does. Shortcuts pressed inside Gecko never come through here;
/// the agent reports them as snapshot events.
/// </remarks>
public static class Shortcuts
{
    private const int Tab = 0x09;
    private const int Left = 0x25;
    private const int Right = 0x27;
    private const int F5 = 0x74;

    /// <summary>
    /// The command bound to a key press, or null. Modifiers must match exactly, so AltGr
    /// combinations (which Windows reports as Ctrl+Alt) never trigger a Ctrl shortcut.
    /// </summary>
    public static BrowserCommand? Match(int virtualKey, ShortcutModifiers modifiers) => (modifiers, virtualKey) switch
    {
        (ShortcutModifiers.Control, 'T') => BrowserCommand.NewTab,
        // Ctrl+N as well, to match what the Gecko agent forwards: Mosaic has one window.
        (ShortcutModifiers.Control, 'N') => BrowserCommand.NewTab,
        (ShortcutModifiers.Control, 'W') => BrowserCommand.CloseTab,
        (ShortcutModifiers.Control, 'L') => BrowserCommand.FocusAddress,
        (ShortcutModifiers.Control, 'R') => BrowserCommand.Reload,
        (ShortcutModifiers.None, F5) => BrowserCommand.Reload,
        (ShortcutModifiers.Alt, Left) => BrowserCommand.Back,
        (ShortcutModifiers.Alt, Right) => BrowserCommand.Forward,
        (ShortcutModifiers.Control, Tab) => BrowserCommand.NextTab,
        (ShortcutModifiers.Control | ShortcutModifiers.Shift, Tab) => BrowserCommand.PreviousTab,
        (ShortcutModifiers.Control | ShortcutModifiers.Shift, 'E') => BrowserCommand.ToggleEngine,
        (ShortcutModifiers.Control | ShortcutModifiers.Shift, 'S') => BrowserCommand.NextSearchEngine,
        (ShortcutModifiers.Control | ShortcutModifiers.Shift, 'X') => BrowserCommand.ShowExtensions,
        _ => null,
    };
}
